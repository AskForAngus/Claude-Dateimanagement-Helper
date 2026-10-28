namespace NasZentrale.Core.Paths;

/// <summary>Hilfen für Pfade auf dem NAS (immer mit „/“, z. B. /mnt/tank/daten).</summary>
public static class NasPath
{
    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (p.Length > 1) p = p.TrimEnd('/');
        return p;
    }

    public static string Parent(string path)
    {
        path = Normalize(path);
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    public static string Name(string path)
    {
        path = Normalize(path);
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    public static string Combine(string folder, string name) => Normalize(folder.TrimEnd('/') + "/" + name.Trim('/'));

    public static int Depth(string path) => Normalize(path).Count(c => c == '/');

    /// <summary>True, wenn <paramref name="path"/> gleich <paramref name="root"/> ist oder darunter liegt.</summary>
    public static bool IsUnder(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        return path == root || path.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal);
    }

    public static string Relative(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        if (path == root) return "";
        return IsUnder(path, root) ? path[(root.Length + 1)..] : path;
    }

    /// <summary>Alle Ordner von <paramref name="root"/> bis einschließlich <paramref name="path"/>.</summary>
    public static IReadOnlyList<string> Chain(string root, string path)
    {
        var list = new List<string>();
        root = Normalize(root);
        var current = Normalize(path);
        while (IsUnder(current, root))
        {
            list.Add(current);
            if (current == root) break;
            current = Parent(current);
        }
        list.Reverse();
        return list;
    }
}
