using System.Text.Json.Nodes;
using NasZentrale.Core.Acl;
using Xunit;

namespace NasZentrale.Core.Tests;

public class AclTests
{
    private const NfsFlag Inherit = NfsFlag.FileInherit | NfsFlag.DirectoryInherit;

    private static NfsAce Group(long id, NfsPerm perms, NfsFlag flags = Inherit, AceType type = AceType.Allow) =>
        new() { Tag = AceTag.Group, Id = id, Who = $"g{id}", Perms = perms, Flags = flags, Type = type };

    private static NfsAce OwnerFull() => new() { Tag = AceTag.Owner, Perms = NfsPerm.FullControl, Flags = Inherit };

    private static NfsAcl Root(params NfsAce[] aces) => new() { Aces = aces.ToList() };

    /// <summary>
    /// /d (Dataset) ─ a ─ a1 ─ f.txt
    ///              └ b (eigene Rechte: Gruppe 20) ─ b1 (eigene Rechte: Gruppe 30)
    ///              └ c (Vererbung deaktiviert)
    /// </summary>
    private static FakeNas Sample()
    {
        var nas = new FakeNas().Dir("/d", Root(OwnerFull(), Group(10, NfsPerm.Read)));
        nas.Dir("/d/a").Dir("/d/a/a1").File("/d/a/a1/f.txt");

        var b = new NfsAcl { Aces = new List<NfsAce> { Group(20, NfsPerm.Modify) } };
        b.Aces.AddRange(AclRules.InheritedFor(nas.Acl("/d").Aces, true));
        nas.Dir("/d/b", b);
        var b1 = new NfsAcl { Aces = new List<NfsAce> { Group(30, NfsPerm.Read) } };
        b1.Aces.AddRange(AclRules.InheritedFor(nas.Acl("/d/b").Aces, true));
        nas.Dir("/d/b/b1", b1).File("/d/b/b1/x.txt");

        nas.Dir("/d/c", new NfsAcl { Protected = true, Aces = { OwnerFull(), Group(40, NfsPerm.FullControl) } });
        nas.Dir("/d/c/c1");
        return nas;
    }

    [Fact]
    public void InheritedFor_follows_nfs4_rules()
    {
        var parent = new[]
        {
            Group(1, NfsPerm.Read, Inherit),
            Group(2, NfsPerm.Read, NfsFlag.None),
            Group(3, NfsPerm.Read, NfsFlag.FileInherit),
            Group(4, NfsPerm.Read, NfsFlag.DirectoryInherit | NfsFlag.InheritOnly),
            Group(5, NfsPerm.Read, Inherit | NfsFlag.NoPropagateInherit),
        };

        var dir = AclRules.InheritedFor(parent, true);
        Assert.Equal(new long?[] { 1, 3, 4, 5 }, dir.Select(a => a.Id));
        Assert.Equal(NfsFlag.Inherited | Inherit, dir[0].Flags);
        Assert.Equal(NfsFlag.Inherited | NfsFlag.FileInherit | NfsFlag.InheritOnly, dir[1].Flags);
        Assert.Equal(NfsFlag.Inherited | NfsFlag.DirectoryInherit, dir[2].Flags);
        Assert.Equal(NfsFlag.Inherited, dir[3].Flags);

        var file = AclRules.InheritedFor(parent, false);
        Assert.Equal(new long?[] { 1, 3, 5 }, file.Select(a => a.Id));
        Assert.All(file, a => Assert.Equal(NfsFlag.Inherited, a.Flags));
    }

    [Fact]
    public async Task Adding_a_group_reaches_everything_but_keeps_custom_subfolder_rights()
    {
        var nas = Sample();
        var before = nas.Acl("/d");
        var after = before.Clone();
        after.Aces.Add(Group(99, NfsPerm.Modify));

        var plan = AclChangePlanner.CreatePlan(before, after, true);
        Assert.True(plan.NeedsPropagation);
        Assert.Single(plan.Changes);
        Assert.Equal(AceChangeKind.Added, plan.Changes[0].Kind);

        var planner = new AclChangePlanner(nas);
        plan.Scan = await planner.ScanAsync(before);
        Assert.Equal(new[] { "/d/b", "/d/c", "/d/b/b1" }, plan.Scan.CustomFolders.Select(c => c.Path));

        await planner.ApplyAsync(plan);

        bool Has(string path, long id) => nas.Acl(path).Aces.Any(a => a.Id == id && !a.IsInheritOnly);

        // Neue Gruppe überall angekommen, auch in Ordnern mit eigenen Rechten …
        foreach (var p in new[] { "/d/a", "/d/a/a1", "/d/a/a1/f.txt", "/d/b", "/d/b/b1", "/d/b/b1/x.txt" })
            Assert.True(Has(p, 99), p);
        // … aber nicht in Ordnern mit deaktivierter Vererbung.
        Assert.False(Has("/d/c", 99));
        Assert.False(Has("/d/c/c1", 99));

        // Eigene Rechte sind erhalten geblieben.
        Assert.Contains(nas.Acl("/d/b").Aces, a => a.Id == 20 && !a.IsInherited);
        Assert.Contains(nas.Acl("/d/b/b1").Aces, a => a.Id == 30 && !a.IsInherited);
        Assert.Contains(nas.Acl("/d/b/b1/x.txt").Aces, a => a.Id == 20 && a.IsInherited);
        Assert.Contains(nas.Acl("/d/b/b1/x.txt").Aces, a => a.Id == 30 && a.IsInherited);
        Assert.True(nas.Acl("/d/c").Protected);
        Assert.Contains(nas.Acl("/d/c/c1").Aces, a => a.Id == 40);
    }

    [Fact]
    public async Task Removing_a_group_removes_it_everywhere_including_custom_folders()
    {
        var nas = Sample();
        var before = nas.Acl("/d");
        var after = before.Clone();
        after.Aces.RemoveAll(a => a.Id == 10);

        var planner = new AclChangePlanner(nas);
        await planner.ApplyAsync(AclChangePlanner.CreatePlan(before, after, true));

        foreach (var p in new[] { "/d", "/d/a", "/d/a/a1/f.txt", "/d/b", "/d/b/b1", "/d/b/b1/x.txt" })
            Assert.DoesNotContain(nas.Acl(p).Aces, a => a.Id == 10);
        Assert.Contains(nas.Acl("/d/b/b1").Aces, a => a.Id == 20 && a.IsInherited);
    }

    [Fact]
    public async Task Undo_restores_the_previous_state_exactly()
    {
        var nas = Sample();
        var paths = new[] { "/d", "/d/a", "/d/a/a1", "/d/a/a1/f.txt", "/d/b", "/d/b/b1", "/d/b/b1/x.txt", "/d/c", "/d/c/c1" };
        var snapshot = paths.ToDictionary(p => p, p => nas.Acl(p));

        var after = nas.Acl("/d").Clone();
        after.Aces.RemoveAll(a => a.Id == 10);
        after.Aces.Add(Group(77, NfsPerm.FullControl));
        var planner = new AclChangePlanner(nas);
        var undo = await planner.ApplyAsync(AclChangePlanner.CreatePlan(nas.Acl("/d"), after, true));

        // Wie im Programm: Rückgängig-Daten gehen durchs Protokoll (JSON).
        var stored = (JsonObject)JsonNode.Parse(undo.ToJsonString())!;
        await planner.UndoAsync(stored);

        foreach (var p in paths)
        {
            var expected = snapshot[p];
            var actual = nas.Acl(p);
            Assert.Equal(expected.Protected, actual.Protected);
            Assert.True(AclRules.SameSet(expected.Aces, actual.Aces), p);
            Assert.Equal(expected.Aces.Count(a => a.IsInherited), actual.Aces.Count(a => a.IsInherited));
        }
    }

    [Fact]
    public async Task Change_without_inheritance_effect_touches_only_the_folder()
    {
        var nas = Sample();
        var before = nas.Acl("/d");
        var after = before.Clone();
        after.Aces.Add(Group(55, NfsPerm.Traverse, NfsFlag.None));

        var plan = AclChangePlanner.CreatePlan(before, after, true);
        Assert.False(plan.NeedsPropagation);
        await new AclChangePlanner(nas).ApplyAsync(plan);
        Assert.Equal(1, nas.SetAclCalls);
        Assert.DoesNotContain(nas.Acl("/d/a").Aces, a => a.Id == 55);
    }

    [Fact]
    public void Copied_entries_without_inherited_flag_count_as_inherited()
    {
        var parent = new[] { OwnerFull(), Group(10, NfsPerm.Read) };
        var expected = AclRules.InheritedFor(parent, true);
        var acl = new NfsAcl
        {
            Aces = expected.Select(a => { var c = a.Clone(); c.Flags &= ~NfsFlag.Inherited; return c; })
                .Append(Group(20, NfsPerm.Modify)).ToList(),
        };
        var own = AclRules.OwnAces(acl, expected);
        Assert.Single(own);
        Assert.Equal(20, own[0].Id);
    }

    [Fact]
    public void Evaluate_respects_order_and_deny()
    {
        var acl = new NfsAcl
        {
            Uid = 1000,
            Gid = 500,
            Aces =
            {
                Group(10, NfsPerm.WriteData | NfsPerm.Delete, Inherit, AceType.Deny),
                Group(10, NfsPerm.Modify),
                new NfsAce { Tag = AceTag.Owner, Perms = NfsPerm.FullControl },
            },
        };
        var member = AclRules.Evaluate(acl, 2000, new long[] { 10 });
        Assert.False(member.HasFlag(NfsPerm.WriteData));
        Assert.False(member.HasFlag(NfsPerm.Delete));
        Assert.True(member.HasFlag(NfsPerm.ReadData));
        Assert.Equal(AccessLevel.Read, AclRules.AtLeastLevel(member));

        Assert.Equal(AccessLevel.Full, AclRules.AtLeastLevel(AclRules.Evaluate(acl, 1000, Array.Empty<long>())));
        Assert.Equal(AccessLevel.None, AclRules.AtLeastLevel(AclRules.Evaluate(acl, 3000, new long[] { 99 })));
    }

    [Fact]
    public void Parses_truenas_json_including_basic_forms()
    {
        var json = JsonNode.Parse("""
        {
          "path": "/mnt/tank/daten", "uid": 0, "gid": 545, "user": "root", "group": "builtin_users",
          "acltype": "NFS4", "trivial": false,
          "aclflags": {"autoinherit": true, "protected": false, "defaulted": false},
          "acl": [
            {"tag": "owner@", "id": -1, "type": "ALLOW", "perms": {"BASIC": "FULL_CONTROL"}, "flags": {"BASIC": "INHERIT"}},
            {"tag": "GROUP", "id": 1103, "who": "FIRMA\\buchhaltung", "type": "ALLOW",
             "perms": {"READ_DATA": true, "EXECUTE": true, "READ_ATTRIBUTES": true, "READ_NAMED_ATTRS": true, "READ_ACL": true, "SYNCHRONIZE": true},
             "flags": {"FILE_INHERIT": true, "DIRECTORY_INHERIT": true, "INHERITED": true}}
          ]
        }
        """)!;

        var acl = AclJson.Parse(json);
        Assert.Equal("builtin_users", acl.OwnerGroupName);
        Assert.True(acl.AutoInherit);
        Assert.Equal(2, acl.Aces.Count);
        Assert.Null(acl.Aces[0].Id);
        Assert.Equal(NfsPerm.FullControl, acl.Aces[0].Perms);
        Assert.Equal(AccessLevel.Read, AclRules.ExactLevel(acl.Aces[1].Perms));
        Assert.True(acl.Aces[1].IsInherited);
        Assert.Equal(@"FIRMA\buchhaltung", acl.Aces[1].DisplayName);

        var payload = AclJson.ToSetAclPayload(acl, true);
        Assert.Equal("NFS4", payload["acltype"]!.ToString());
        Assert.True(payload["options"]!["recursive"]!.GetValue<bool>());
        var ace = payload["dacl"]![1]!;
        Assert.Equal("GROUP", ace["tag"]!.ToString());
        Assert.Equal(1103, ace["id"]!.GetValue<long>());
        Assert.True(ace["flags"]!["INHERITED"]!.GetValue<bool>());
        Assert.False(ace["perms"]!["WRITE_DATA"]!.GetValue<bool>());

        var roundTrip = AclJson.Parse(AclJson.ToStorage(acl));
        Assert.True(AclRules.SameSet(acl.Aces, roundTrip.Aces));
    }

    [Theory]
    [InlineData(ApplyTo.ThisFolderSubfoldersFiles)]
    [InlineData(ApplyTo.ThisFolderOnly)]
    [InlineData(ApplyTo.ThisFolderSubfolders)]
    [InlineData(ApplyTo.ThisFolderFiles)]
    [InlineData(ApplyTo.SubfoldersFilesOnly)]
    [InlineData(ApplyTo.SubfoldersOnly)]
    [InlineData(ApplyTo.FilesOnly)]
    public void ApplyTo_round_trips(ApplyTo a)
    {
        Assert.Equal(a, AclRules.ApplyToFromFlags(AclRules.WithApplyTo(NfsFlag.Inherited, a)));
    }
}
