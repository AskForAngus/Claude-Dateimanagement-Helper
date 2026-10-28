using NasZentrale.Core.Accounts;
using NasZentrale.Core.Paths;
using NasZentrale.Core.TrueNas;

namespace NasZentrale.Core.Acl;

/// <summary>Ein Ordner, an dem sich Rechte ändern: Dataset-Wurzel oder Ordner mit eigenen Rechten.</summary>
public sealed record IndexedFolder(string Path, string DatasetMount, NfsAcl Acl, bool IsDatasetRoot, IReadOnlyList<NfsAce> OwnAces)
{
    public string Name => IsDatasetRoot ? NasPath.Name(Path) : NasPath.Relative(Path, DatasetMount);
}

public sealed record AccessResult(IndexedFolder Folder, NfsPerm Perms, AccessLevel Level, IReadOnlyList<string> Via);

/// <summary>Überblick über alle Stellen, an denen Rechte gesetzt sind – Grundlage für „Wer darf wo?“.</summary>
public sealed class AccessIndex
{
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public List<IndexedFolder> Folders { get; } = new();
    public int TotalDirectories { get; set; }

    public static async Task<AccessIndex> BuildAsync(IAclBackend backend, IEnumerable<DatasetInfo> datasets, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var index = new AccessIndex();
        var planner = new AclChangePlanner(backend);
        foreach (var ds in datasets.Where(d => d.IsNfs4))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Lese {ds.Name} …");
            var rootAcl = await backend.GetAclAsync(ds.Mountpoint, ct);
            index.Folders.Add(new IndexedFolder(ds.Mountpoint, ds.Mountpoint, rootAcl, true, rootAcl.ExplicitAces.ToList()));
            var scan = await planner.ScanAsync(rootAcl, new Progress<string>(s => progress?.Report($"{ds.ShortName}: {s}")), ct);
            index.TotalDirectories += scan.Directories;
            index.Folders.AddRange(scan.CustomFolders.Select(c => new IndexedFolder(c.Path, ds.Mountpoint, c.OriginalAcl, false, c.OwnAces)));
        }
        return index;
    }

    public bool IsCustom(string path) => Folders.Any(f => !f.IsDatasetRoot && f.Path == NasPath.Normalize(path));

    /// <summary>Effektive Rechte eines Benutzers/einer Gruppe an allen bekannten Stellen.</summary>
    public IReadOnlyList<AccessResult> Evaluate(Principal principal, IReadOnlyCollection<long> userGroupIds)
    {
        var uid = principal.Kind == PrincipalKind.User ? principal.Id : long.MinValue;
        var gids = principal.Kind == PrincipalKind.Group ? new[] { principal.Id } : userGroupIds;
        return Folders.Select(f =>
        {
            var perms = AclRules.Evaluate(f.Acl, uid, gids);
            var via = f.Acl.Aces
                .Where(a => a.Type == AceType.Allow && !a.IsInheritOnly && Matches(a, f.Acl, uid, gids))
                .Select(a => a.DisplayName)
                .Distinct()
                .ToList();
            return new AccessResult(f, perms, AclRules.AtLeastLevel(perms), via);
        }).ToList();
    }

    private static bool Matches(NfsAce a, NfsAcl acl, long uid, IReadOnlyCollection<long> gids) => a.Tag switch
    {
        AceTag.Everyone => true,
        AceTag.Owner => uid == acl.Uid,
        AceTag.OwnerGroup => gids.Contains(acl.Gid),
        AceTag.User => a.Id == uid,
        AceTag.Group => a.Id is { } id && gids.Contains(id),
        _ => false,
    };
}
