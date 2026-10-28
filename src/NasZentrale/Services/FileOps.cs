using System.IO;

namespace NasZentrale.Services;

/// <summary>Dateiaktionen über SMB-Pfade (\\nas\freigabe\…). Arbeitet mit den Rechten des angemeldeten AD-Kontos.</summary>
public static class FileOps
{
    public static string CreateFolder(string parentUnc, string name)
    {
        var path = Path.Combine(parentUnc, name);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException($"„{name}“ existiert hier bereits.");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Rename(string unc, string newName)
    {
        var parent = Path.GetDirectoryName(unc)!;
        var target = Path.Combine(parent, newName);
        if (string.Equals(unc, target, StringComparison.Ordinal)) return target;
        var caseOnly = string.Equals(unc, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
            throw new IOException($"„{newName}“ existiert hier bereits.");

        if (Directory.Exists(unc))
        {
            if (caseOnly)
            {
                // Nur Groß-/Kleinschreibung geändert: über Zwischennamen umbenennen.
                var temp = target + ".~umbenennen";
                Directory.Move(unc, temp);
                Directory.Move(temp, target);
            }
            else Directory.Move(unc, target);
        }
        else File.Move(unc, target);
        return target;
    }

    public static async Task DeleteAsync(IReadOnlyList<string> items, IProgress<string>? progress, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var i = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Lösche ({++i}/{items.Count}) {Path.GetFileName(item)} …");
                if (Directory.Exists(item)) Directory.Delete(item, true);
                else if (File.Exists(item)) File.Delete(item);
            }
        }, ct);
    }

    /// <summary>Kopiert Dateien/Ordner (auch vom eigenen PC = Hochladen). Bei Namensgleichheit wird „(2)“ angehängt.</summary>
    public static async Task<int> CopyAsync(IReadOnlyList<string> sources, string targetFolder, IProgress<string>? progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var total = sources.Sum(s => Directory.Exists(s) ? Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories).Count() : 1);
            var done = 0;
            foreach (var src in sources)
            {
                EnsureNotInside(src, targetFolder);
                var dest = UniqueName(Path.Combine(targetFolder, Path.GetFileName(src.TrimEnd('\\'))));
                CopyItem(src, dest, ref done, total, progress, ct);
            }
            return done;
        }, ct);
    }

    /// <summary>Verschiebt Elemente. Innerhalb derselben Freigabe ist das sofort erledigt (Umbenennung auf dem NAS).</summary>
    public static async Task MoveAsync(IReadOnlyList<string> sources, string targetFolder, IProgress<string>? progress, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();
                EnsureNotInside(src, targetFolder);
                if (string.Equals(Path.GetDirectoryName(src), targetFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                var dest = UniqueName(Path.Combine(targetFolder, Path.GetFileName(src)));
                progress?.Report($"Verschiebe {Path.GetFileName(src)} …");

                if (SameShare(src, targetFolder))
                {
                    if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest);
                }
                else
                {
                    var done = 0;
                    var total = Directory.Exists(src) ? Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Count() : 1;
                    CopyItem(src, dest, ref done, total, progress, ct);
                    if (Directory.Exists(src)) Directory.Delete(src, true); else File.Delete(src);
                }
            }
        }, ct);
    }

    private static void CopyItem(string src, string dest, ref int done, int total, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dest);
            foreach (var entry in Directory.EnumerateFileSystemEntries(src))
                CopyItem(entry, Path.Combine(dest, Path.GetFileName(entry)), ref done, total, progress, ct);
        }
        else
        {
            progress?.Report($"Kopiere ({done + 1}/{total}) {Path.GetFileName(src)} …");
            File.Copy(src, dest, false);
            done++;
        }
    }

    private static void EnsureNotInside(string src, string targetFolder)
    {
        var s = src.TrimEnd('\\') + "\\";
        var t = targetFolder.TrimEnd('\\') + "\\";
        if (Directory.Exists(src) && t.StartsWith(s, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"„{Path.GetFileName(src)}“ kann nicht in sich selbst kopiert oder verschoben werden.");
    }

    private static bool SameShare(string a, string b)
    {
        static string Root(string p) => string.Join('\\', p.TrimStart('\\').Split('\\').Take(2)).ToLowerInvariant();
        return a.StartsWith(@"\\") && b.StartsWith(@"\\") && Root(a) == Root(b);
    }

    public static string UniqueName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var isDir = Directory.Exists(path);
        var name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
