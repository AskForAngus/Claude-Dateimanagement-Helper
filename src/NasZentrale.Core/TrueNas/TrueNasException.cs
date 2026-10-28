using System.Text.Json.Nodes;

namespace NasZentrale.Core.TrueNas;

/// <summary>Fehler, den TrueNAS selbst gemeldet hat (z. B. fehlende Rechte, ungültige Eingabe).</summary>
public class TrueNasException : Exception
{
    public TrueNasException(string message, string? errorName = null, Exception? inner = null) : base(message, inner)
    {
        ErrorName = errorName;
    }

    public string? ErrorName { get; }

    /// <summary>True, wenn TrueNAS die Methode nicht kennt (andere Version).</summary>
    public bool IsMethodNotFound => ErrorName is "ENOMETHOD" or "-32601";

    public static TrueNasException FromRpcError(JsonObject error)
    {
        var code = error["code"]?.ToString();
        var data = error["data"] as JsonObject;
        var reason = data?["reason"]?.ToString();
        var errname = data?["errname"]?.ToString() ?? code;
        var message = !string.IsNullOrWhiteSpace(reason) ? reason! : error["message"]?.ToString() ?? "Unbekannter Fehler";

        // Validierungsfehler enthalten unter "extra" eine Liste [Feld, Meldung, Fehlernummer].
        if (data?["extra"] is JsonArray extra && extra.Count > 0)
        {
            var lines = extra.OfType<JsonArray>()
                .Where(e => e.Count >= 2)
                .Select(e => $"{e[0]}: {e[1]}")
                .ToList();
            if (lines.Count > 0) message = string.Join(Environment.NewLine, lines);
        }

        if (code == "-32601") errname = "-32601";
        return new TrueNasException(message.Trim(), errname);
    }
}

/// <summary>Netzwerk- oder Verbindungsproblem.</summary>
public class TrueNasConnectionException : Exception
{
    public TrueNasConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Das Zertifikat des NAS ist (noch) nicht vertrauenswürdig – der Benutzer muss es bestätigen.</summary>
public sealed class CertificateNotTrustedException : Exception
{
    public CertificateNotTrustedException(string thumbprint, string subject)
        : base("Das Zertifikat des NAS ist nicht vertrauenswürdig.")
    {
        Thumbprint = thumbprint;
        Subject = subject;
    }

    public string Thumbprint { get; }
    public string Subject { get; }
}
