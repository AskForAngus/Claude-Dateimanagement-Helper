using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NasZentrale.Core.TrueNas;

namespace NasZentrale.Core.Protocol;

public static class ProtocolCategory
{
    public const string Rights = "Rechte";
    public const string Files = "Dateien";
    public const string Shares = "Freigaben";
    public const string Datasets = "Datasets";
}

/// <summary>Ein Protokolleintrag: wer hat wann was geändert.</summary>
public sealed class ProtocolEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string User { get; set; } = "";
    public string Machine { get; set; } = Environment.MachineName;
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Details { get; set; } = new();
    /// <summary>Daten zum Rückgängigmachen (nur bei Rechteänderungen).</summary>
    public JsonObject? Undo { get; set; }
    /// <summary>Gesetzt, wenn dieser Eintrag eine frühere Änderung rückgängig macht.</summary>
    public Guid? UndoOf { get; set; }
}

/// <summary>
/// Gemeinsames Protokoll aller Admins: ein Ordner (normalerweise auf dem NAS) mit einer JSON-Lines-Datei pro Monat.
/// Mehrere Programme können gleichzeitig anhängen.
/// </summary>
public sealed class ProtocolStore
{
    private static readonly JsonSerializerOptions Json = new(JsonExt.Options) { WriteIndented = false };

    public ProtocolStore(string folder) => Folder = folder;

    public string Folder { get; }

    private string FileFor(DateTimeOffset t) => Path.Combine(Folder, $"protokoll-{t:yyyy-MM}.jsonl");

    public async Task AppendAsync(ProtocolEntry entry)
    {
        Directory.CreateDirectory(Folder);
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(FileFor(entry.Time), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                await fs.WriteAsync(bytes);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(100 + attempt * 100); // Datei gerade von einem anderen Admin gesperrt
            }
        }
    }

    public async Task<List<ProtocolEntry>> LoadAsync(int months = 12)
    {
        var result = new List<ProtocolEntry>();
        if (!Directory.Exists(Folder)) return result;

        var now = DateTimeOffset.Now;
        for (var i = 0; i < months; i++)
        {
            var file = FileFor(now.AddMonths(-i));
            if (!File.Exists(file)) continue;
            string text;
            await using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                text = await reader.ReadToEndAsync();

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ProtocolEntry>(line, Json) is { } e) result.Add(e);
                }
                catch (JsonException) { /* defekte Zeile überspringen */ }
            }
        }
        return result.OrderByDescending(e => e.Time).ToList();
    }
}
