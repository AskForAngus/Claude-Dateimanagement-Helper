using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NasZentrale.Core.Paths;
using NasZentrale.Core.TrueNas;

namespace NasZentrale.Core.Acl;

/// <summary>Ein Unterordner mit eigenen Rechten, die beim Weitergeben erhalten bleiben.</summary>
public sealed record CustomFolder(string Path, NfsAcl OriginalAcl, List<NfsAce> OwnAces)
{
    public bool Protected => OriginalAcl.Protected;
}

public sealed class AclScanResult
{
    public int Directories { get; set; }
    public int Files { get; set; }
    public List<CustomFolder> CustomFolders { get; } = new();
    public List<string> ChildDatasets { get; } = new();
}

public enum AceChangeKind { Added, Removed, Changed }

public sealed record AceChange(AceChangeKind Kind, NfsAce? Before, NfsAce? After)
{
    public string Text
    {
        get
        {
            var ace = After ?? Before!;
            var deny = ace.Type == AceType.Deny ? " (Verweigern)" : "";
            return Kind switch
            {
                AceChangeKind.Added => $"{ace.DisplayName} erhält {AclRules.Summary(ace.Perms)}{deny} – {AclRules.ApplyToFromFlags(ace.Flags).Label()}",
                AceChangeKind.Removed => $"{ace.DisplayName} verliert {AclRules.Summary(Before!.Perms)}{deny}",
                _ => $"{ace.DisplayName}: {DescribeChange(Before!, After!)}",
            };
        }
    }

    /// <summary>Nur nennen, was sich wirklich geändert hat (Rechte und/oder Geltungsbereich).</summary>
    private static string DescribeChange(NfsAce before, NfsAce after)
    {
        var parts = new List<string>();
        if (before.Perms != after.Perms)
            parts.Add($"{AclRules.Summary(before.Perms)} → {AclRules.Summary(after.Perms)}");
        var applyBefore = AclRules.ApplyToFromFlags(before.Flags);
        var applyAfter = AclRules.ApplyToFromFlags(after.Flags);
        if (applyBefore != applyAfter)
            parts.Add($"gilt jetzt für „{applyAfter.Label()}“ (vorher „{applyBefore.Label()}“)");
        if ((before.Flags & NfsFlag.NoPropagateInherit) != (after.Flags & NfsFlag.NoPropagateInherit))
            parts.Add(after.Flags.HasFlag(NfsFlag.NoPropagateInherit) ? "nur noch an direkte Unterobjekte" : "an alle Ebenen darunter");
        return parts.Count == 0 ? "unverändert" : string.Join("; ", parts);
    }
}

/// <summary>Was beim Speichern passieren wird – Grundlage für die Vorschau.</summary>
public sealed class AclChangePlan
{
    public required NfsAcl Before { get; init; }
    public required NfsAcl After { get; init; }
    public required bool IsDirectory { get; init; }
    public List<AceChange> Changes { get; } = new();
    public bool ProtectionChanged => Before.Protected != After.Protected;
    /// <summary>Müssen Unterordner/Dateien angepasst werden?</summary>
    public bool NeedsPropagation { get; init; }
    public AclScanResult? Scan { get; set; }
}

/// <summary>
/// Setzt Rechte wie der Windows-Explorer: Änderungen werden an Unterordner und Dateien weitergegeben,
/// Unterordner mit eigenen Rechten behalten diese. TrueNAS selbst kann das nicht (rekursives Setzen
/// überschreibt alles), deshalb wird vorher gescannt und danach gezielt wiederhergestellt.
/// </summary>
public sealed class AclChangePlanner
{
    private readonly IAclBackend _backend;

    public AclChangePlanner(IAclBackend backend) => _backend = backend;

    public int Parallelism { get; set; } = 8;

    public static AclChangePlan CreatePlan(NfsAcl before, NfsAcl after, bool isDirectory)
    {
        var plan = new AclChangePlan
        {
            Before = before,
            After = after,
            IsDirectory = isDirectory,
            NeedsPropagation = isDirectory && !AclRules.SameInheritance(before.Aces, after.Aces),
        };

        var oldList = before.ExplicitAces.ToList();
        var newList = after.ExplicitAces.ToList();
        if (before.Protected != after.Protected)
        {
            // Beim (De-)Aktivieren der Vererbung werden geerbte Einträge zu eigenen oder umgekehrt – nicht als Änderung zählen.
            oldList = before.Aces.ToList();
            newList = after.Aces.ToList();
        }

        foreach (var n in newList)
        {
            var match = oldList.FirstOrDefault(o => o.SamePrincipal(n) && o.Type == n.Type);
            if (match == null) plan.Changes.Add(new AceChange(AceChangeKind.Added, null, n));
            else
            {
                oldList.Remove(match);
                if (match.Perms != n.Perms || (match.Flags & ~NfsFlag.Inherited) != (n.Flags & ~NfsFlag.Inherited))
                    plan.Changes.Add(new AceChange(AceChangeKind.Changed, match, n));
            }
        }
        plan.Changes.AddRange(oldList.Select(o => new AceChange(AceChangeKind.Removed, o, null)));
        return plan;
    }

    /// <summary>Liest alle Unterordner ein und merkt sich die mit eigenen Rechten.</summary>
    public async Task<AclScanResult> ScanAsync(NfsAcl rootAcl, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new AclScanResult();
        var counts = new int[2]; // [0] Ordner, [1] Dateien
        var customs = new ConcurrentBag<CustomFolder>();
        var level = new List<(string Path, NfsAcl Acl)> { (rootAcl.Path, rootAcl) };
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };

        while (level.Count > 0)
        {
            var next = new ConcurrentBag<(string Path, NfsAcl Acl)>();
            await Parallel.ForEachAsync(level, options, async (parent, token) =>
            {
                var entries = await _backend.ListDirAsync(parent.Path, token);
                var expectedForDirs = AclRules.InheritedFor(parent.Acl.Aces, true);
                foreach (var e in entries)
                {
                    if (!e.IsDirectory || e.IsSymlink)
                    {
                        Interlocked.Increment(ref counts[1]);
                        continue;
                    }
                    if (e.IsMountpoint)
                    {
                        lock (result.ChildDatasets) result.ChildDatasets.Add(e.Path);
                        continue;
                    }
                    var acl = await _backend.GetAclAsync(e.Path, token);
                    var dirs = Interlocked.Increment(ref counts[0]);
                    if (dirs % 50 == 0) progress?.Report($"{dirs} Ordner geprüft …");
                    var own = AclRules.OwnAces(acl, expectedForDirs);
                    if (acl.Protected || own.Count > 0)
                        customs.Add(new CustomFolder(e.Path, acl, own));
                    next.Add((e.Path, acl));
                }
            });
            level = next.ToList();
        }

        result.Directories = counts[0];
        result.Files = counts[1];
        result.CustomFolders.AddRange(customs.OrderBy(c => NasPath.Depth(c.Path)).ThenBy(c => c.Path, StringComparer.Ordinal));
        return result;
    }

    /// <summary>Wendet den Plan an. Liefert die Daten zum Rückgängigmachen.</summary>
    public async Task<JsonObject> ApplyAsync(AclChangePlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var undo = new JsonObject
        {
            ["kind"] = "acl",
            ["root"] = AclJson.ToStorage(plan.Before),
            ["recursive"] = plan.NeedsPropagation,
            ["customs"] = new JsonArray(),
        };

        if (!plan.NeedsPropagation)
        {
            progress?.Report("Rechte werden gesetzt …");
            await _backend.SetAclAsync(plan.After, false, ct);
            return undo;
        }

        var scan = plan.Scan ?? await ScanAsync(plan.Before, progress, ct);
        foreach (var c in scan.CustomFolders)
            ((JsonArray)undo["customs"]!).Add(AclJson.ToStorage(c.OriginalAcl));

        progress?.Report("Rechte werden an alle Unterordner und Dateien weitergegeben …");
        await _backend.SetAclAsync(plan.After, true, ct);

        await RestoreCustomFoldersAsync(plan.After, scan.CustomFolders, progress, ct);
        return undo;
    }

    /// <summary>Setzt die eigenen Rechte der Unterordner wieder – kombiniert mit der neuen Vererbung von oben.</summary>
    private async Task RestoreCustomFoldersAsync(NfsAcl newRoot, IReadOnlyList<CustomFolder> customs, IProgress<string>? progress, CancellationToken ct)
    {
        var newAces = new Dictionary<string, List<NfsAce>>(StringComparer.Ordinal) { [NasPath.Normalize(newRoot.Path)] = newRoot.Aces };
        var rootPath = NasPath.Normalize(newRoot.Path);

        List<NfsAce> AcesAfter(string path)
        {
            path = NasPath.Normalize(path);
            if (newAces.TryGetValue(path, out var known)) return known;
            if (path == rootPath || !NasPath.IsUnder(path, rootPath)) return newRoot.Aces;
            var computed = AclRules.InheritedFor(AcesAfter(NasPath.Parent(path)), true);
            newAces[path] = computed;
            return computed;
        }

        var i = 0;
        foreach (var c in customs.OrderBy(c => NasPath.Depth(c.Path)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Eigene Rechte wiederherstellen ({++i}/{customs.Count}): {NasPath.Name(c.Path)}");
            var acl = c.OriginalAcl.Clone();
            if (!c.Protected)
                acl.Aces = c.OwnAces.Select(a => a.Clone()).Concat(AclRules.InheritedFor(AcesAfter(NasPath.Parent(c.Path)), true)).ToList();
            newAces[NasPath.Normalize(c.Path)] = acl.Aces;
            await _backend.SetAclAsync(acl, true, ct);
        }
    }

    /// <summary>Macht eine frühere Änderung rückgängig (Daten aus <see cref="ApplyAsync"/>).</summary>
    public async Task UndoAsync(JsonObject undo, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var root = AclJson.Parse(undo["root"]!);
        var recursive = undo.Bool("recursive");
        progress?.Report("Alte Rechte werden wiederhergestellt …");
        await _backend.SetAclAsync(root, recursive, ct);
        if (!recursive) return;

        var customs = undo["customs"].Objects().Select(o => AclJson.Parse(o)).OrderBy(a => NasPath.Depth(a.Path)).ToList();
        var i = 0;
        foreach (var acl in customs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Unterordner wiederherstellen ({++i}/{customs.Count}) …");
            await _backend.SetAclAsync(acl, true, ct);
        }
    }
}
