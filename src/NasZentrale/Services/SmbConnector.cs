using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace NasZentrale.Services;

/// <summary>
/// Meldet Windows mit dem AD-Konto an den SMB-Freigaben an (wie „net use“ ohne Laufwerksbuchstaben).
/// Nötig, weil die PCs nicht in der Domäne sind.
/// </summary>
public sealed class SmbConnector
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorBadNetPath = 53;
    private const int ErrorBadNetName = 67;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    private readonly HashSet<string> _connected = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _username;
    private readonly string _password;

    public SmbConnector(string username, string password)
    {
        _username = username;
        _password = password;
    }

    /// <summary>Stellt sicher, dass auf \\nas\freigabe zugegriffen werden kann.</summary>
    public async Task EnsureConnectedAsync(string shareRootUnc)
    {
        lock (_connected)
            if (_connected.Contains(shareRootUnc)) return;

        var code = await Task.Run(() =>
        {
            var res = new NetResource { Type = ResourceTypeDisk, RemoteName = shareRootUnc };
            return WNetAddConnection2(ref res, _password, _username, 0);
        });

        if (code == ErrorSessionCredentialConflict)
        {
            // Windows ist schon (z. B. über den Explorer) mit dem NAS verbunden. Wenn das funktioniert, ist es auch gut.
            if (await Task.Run(() => Directory.Exists(shareRootUnc)))
            {
                lock (_connected) _connected.Add(shareRootUnc);
                return;
            }
            throw new IOException(
                "Windows ist bereits mit anderen Anmeldedaten mit dem NAS verbunden. " +
                "Trenne die vorhandene Verbindung (z. B. Netzlaufwerk im Explorer) oder melde dich mit der IP-Adresse statt des Namens an.");
        }

        if (code != 0)
        {
            var text = code switch
            {
                ErrorLogonFailure => "Die Anmeldung an der Freigabe ist fehlgeschlagen (Benutzername oder Passwort).",
                ErrorBadNetPath => "Das NAS ist per SMB nicht erreichbar.",
                ErrorBadNetName => "Die Freigabe wurde nicht gefunden.",
                ErrorAccessDenied => "Kein Zugriff auf die Freigabe.",
                _ => new Win32Exception(code).Message,
            };
            throw new IOException($"{text} ({shareRootUnc})");
        }

        lock (_connected) _connected.Add(shareRootUnc);
    }

    public void DisconnectAll()
    {
        string[] list;
        lock (_connected)
        {
            list = _connected.ToArray();
            _connected.Clear();
        }
        foreach (var unc in list)
        {
            try { WNetCancelConnection2(unc, 0, true); } catch { /* beim Abmelden egal */ }
        }
    }
}
