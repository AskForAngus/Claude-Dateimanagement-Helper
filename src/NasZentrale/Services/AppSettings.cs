using System.IO;
using System.Text.Json;

namespace NasZentrale.Services;

/// <summary>Lokale Einstellungen pro Windows-Benutzer (%APPDATA%\NAS-Zentrale).</summary>
public sealed class AppSettings
{
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public bool Remember { get; set; } = true;
    /// <summary>Bestätigte Zertifikats-Fingerabdrücke je NAS-Adresse.</summary>
    public Dictionary<string, string> TrustedCertificates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Protokollordner als NAS-Pfad (z. B. /mnt/tank/verwaltung/.nas-zentrale).</summary>
    public string ProtocolFolder { get; set; } = "";
    /// <summary>AD-Gruppe der Admins (für den Protokollordner).</summary>
    public string AdminGroup { get; set; } = "";
    public bool SetupDone { get; set; }
    public string LastFolder { get; set; } = "";

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NAS-Zentrale");

    private static string FilePath => Path.Combine(AppDataFolder, "einstellungen.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception) { /* beschädigte Datei: mit Standardwerten starten */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { /* Einstellungen sind nicht kritisch */ }
    }
}
