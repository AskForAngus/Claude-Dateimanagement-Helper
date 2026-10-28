using System.IO;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Accounts;
using NasZentrale.Core.Paths;
using NasZentrale.Core.Protocol;
using NasZentrale.Core.TrueNas;

namespace NasZentrale.Services;

/// <summary>Alles, was während einer Anmeldung gilt: Verbindung, Daten-Caches, Protokoll.</summary>
public sealed class Session
{
    private readonly string _password;

    public Session(TrueNasClient client, string host, string loginName, string password)
    {
        Client = client;
        Nas = new TrueNasService(client);
        Host = host;
        LoginName = loginName;
        _password = password;
        Smb = new SmbConnector(loginName, password);
        Protocol = new ProtocolStore(Path.Combine(AppSettings.AppDataFolder, "Protokoll"));
    }

    public static Session Current { get; set; } = null!;

    public TrueNasClient Client { get; }
    public TrueNasService Nas { get; }
    public string Host { get; }
    public string LoginName { get; }
    /// <summary>Rechnername für Windows-Pfade (\\nas\freigabe) – ohne Protokoll und Port.</summary>
    public string SmbHost => Client.Uri.Host;
    public SmbConnector Smb { get; private set; }
    public AppSettings Settings { get; } = AppSettings.Load();

    public CurrentUser Me { get; private set; } = new("?", -1, Array.Empty<long>(), Array.Empty<string>());
    public string Version { get; private set; } = "";
    public string? Workgroup { get; private set; }
    public IReadOnlyList<DatasetInfo> Datasets { get; private set; } = Array.Empty<DatasetInfo>();
    public IReadOnlyList<SmbShare> Shares { get; private set; } = Array.Empty<SmbShare>();
    public ShareMapper Mapper { get; private set; } = new("", Array.Empty<SmbShareInfo>());
    public IReadOnlyList<Principal> Principals { get; private set; } = Array.Empty<Principal>();
    public AccessIndex? Index { get; set; }

    public ProtocolStore Protocol { get; private set; }
    public bool ProtocolIsShared { get; private set; }
    public string? ProtocolProblem { get; private set; }

    public event Action? SharesChanged;
    public event Action? DatasetsChanged;
    public event Action? ProtocolChanged;

    /// <summary>Name für SMB, z. B. FIRMA\max.</summary>
    public string SmbUserName =>
        LoginName.Contains('\\') || LoginName.Contains('@') || string.IsNullOrEmpty(Workgroup) ? LoginName : $@"{Workgroup}\{LoginName}";

    public async Task InitializeAsync()
    {
        Me = await Nas.GetCurrentUserAsync();
        Version = await Nas.GetVersionAsync();
        Workgroup = await Nas.GetSmbWorkgroupAsync();
        Smb = new SmbConnector(SmbUserName, _password);
        await RefreshDatasetsAsync();
        await RefreshSharesAsync();
        Principals = await Nas.GetPrincipalsAsync();
        await OpenProtocolAsync();
    }

    public async Task RefreshDatasetsAsync()
    {
        Datasets = await Nas.GetDatasetsAsync();
        DatasetsChanged?.Invoke();
    }

    public async Task RefreshSharesAsync()
    {
        Shares = await Nas.GetSharesAsync();
        Mapper = new ShareMapper(SmbHost, Shares.Select(s => s.Info));
        SharesChanged?.Invoke();
    }

    public async Task RefreshPrincipalsAsync() => Principals = await Nas.GetPrincipalsAsync();

    /// <summary>Dataset, zu dem ein Pfad gehört (längster passender Einhängepunkt).</summary>
    public DatasetInfo? DatasetFor(string path) =>
        Datasets.Where(d => NasPath.IsUnder(path, d.Mountpoint)).OrderByDescending(d => d.Mountpoint.Length).FirstOrDefault();

    /// <summary>Windows-Pfad für einen NAS-Pfad, vorher wird die Freigabe angemeldet. Null, wenn nicht freigegeben.</summary>
    public async Task<string?> UncForAsync(string nasPath)
    {
        var root = Mapper.ShareRootUnc(nasPath);
        if (root == null) return null;
        await Smb.EnsureConnectedAsync(root);
        return Mapper.ToUnc(nasPath);
    }

    public async Task OpenProtocolAsync()
    {
        ProtocolProblem = null;
        ProtocolIsShared = false;
        Protocol = new ProtocolStore(Path.Combine(AppSettings.AppDataFolder, "Protokoll"));
        if (string.IsNullOrWhiteSpace(Settings.ProtocolFolder)) return;

        try
        {
            var unc = await UncForAsync(Settings.ProtocolFolder);
            if (unc == null)
            {
                ProtocolProblem = "Der Protokollordner liegt in keiner aktiven Freigabe.";
                return;
            }
            Directory.CreateDirectory(unc);
            Protocol = new ProtocolStore(unc);
            ProtocolIsShared = true;
        }
        catch (Exception ex)
        {
            ProtocolProblem = $"Gemeinsames Protokoll nicht erreichbar: {ex.Message}";
        }
        ProtocolChanged?.Invoke();
    }

    public async Task LogAsync(ProtocolEntry entry)
    {
        entry.User = Me.Name;
        try
        {
            await Protocol.AppendAsync(entry);
        }
        catch (Exception)
        {
            // Wenn das NAS-Protokoll gerade nicht geht, wenigstens lokal festhalten.
            await new ProtocolStore(Path.Combine(AppSettings.AppDataFolder, "Protokoll")).AppendAsync(entry);
        }
        ProtocolChanged?.Invoke();
    }

    /// <summary>Nach Verbindungsabbruch automatisch neu verbinden und anmelden.</summary>
    public async Task<bool> ReconnectAsync()
    {
        try
        {
            await Client.ConnectAsync();
            return await Nas.LoginAsync(LoginName, _password) == LoginOutcome.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task CloseAsync()
    {
        Smb.DisconnectAll();
        await Client.DisposeAsync();
    }
}
