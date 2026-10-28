namespace NasZentrale.Core.Paths;

public sealed record SmbShareInfo(long Id, string Name, string Path, bool Enabled);

/// <summary>Übersetzt NAS-Pfade in Windows-Netzwerkpfade (\\nas\freigabe\ordner) und umgekehrt.</summary>
public sealed class ShareMapper
{
    private readonly List<SmbShareInfo> _shares;

    public ShareMapper(string host, IEnumerable<SmbShareInfo> shares)
    {
        Host = host;
        // Längster Pfad zuerst, damit verschachtelte Freigaben gewinnen.
        _shares = shares
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Path))
            .Select(s => s with { Path = NasPath.Normalize(s.Path) })
            .OrderByDescending(s => s.Path.Length)
            .ToList();
    }

    public string Host { get; }

    public SmbShareInfo? ShareFor(string nasPath)
    {
        var p = NasPath.Normalize(nasPath);
        return _shares.FirstOrDefault(s => NasPath.IsUnder(p, s.Path));
    }

    public string? ToUnc(string nasPath)
    {
        var share = ShareFor(nasPath);
        if (share == null) return null;
        var rel = NasPath.Relative(nasPath, share.Path);
        var unc = $@"\\{Host}\{share.Name}";
        return rel.Length == 0 ? unc : unc + @"\" + rel.Replace('/', '\\');
    }

    public string? ShareRootUnc(string nasPath)
    {
        var share = ShareFor(nasPath);
        return share == null ? null : $@"\\{Host}\{share.Name}";
    }
}
