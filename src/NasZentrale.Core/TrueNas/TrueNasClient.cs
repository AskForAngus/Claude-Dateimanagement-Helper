using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NasZentrale.Core.TrueNas;

/// <summary>Minimale Schnittstelle für JSON-RPC-Aufrufe – erlaubt Test-Doubles.</summary>
public interface ITrueNasApi
{
    Task<JsonNode?> CallAsync(string method, IReadOnlyList<object?> args, CancellationToken ct = default);
    Task<JsonNode?> CallJobAsync(string method, IReadOnlyList<object?> args, IProgress<JobProgress>? progress = null, CancellationToken ct = default);
}

public sealed record JobProgress(double Percent, string? Description);

/// <summary>
/// JSON-RPC-2.0-Client für die TrueNAS-API (wss://host/api/current, ab TrueNAS 25.04).
/// Ein Socket, beliebig viele parallele Aufrufe.
/// </summary>
public sealed class TrueNasClient : ITrueNasApi, IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loopCts;
    private Timer? _keepAlive;
    private long _nextId;

    public TrueNasClient(string host, string? pinnedThumbprint)
    {
        Uri = BuildUri(host);
        PinnedThumbprint = pinnedThumbprint;
    }

    public Uri Uri { get; }
    public string? PinnedThumbprint { get; set; }
    public string? LastCertificateThumbprint { get; private set; }
    public string? LastCertificateSubject { get; private set; }
    public bool LastCertificateWasValid { get; private set; }
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Wird ausgelöst, wenn die Verbindung unerwartet abbricht.</summary>
    public event EventHandler? Disconnected;

    public static Uri BuildUri(string host)
    {
        host = host.Trim().TrimEnd('/');
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = "ws://" + host[7..];
        else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = "wss://" + host[8..];
        else if (!host.Contains("://")) host = "wss://" + host;
        var b = new UriBuilder(host);
        if (b.Path is "" or "/") b.Path = "/api/current";
        return b.Uri;
    }

    public static string FormatThumbprint(byte[] hash) => string.Join(":", hash.Select(x => x.ToString("X2")));

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await CloseSocketAsync();
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.RemoteCertificateValidationCallback = ValidateCertificate;
        try
        {
            await ws.ConnectAsync(Uri, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or System.Security.Authentication.AuthenticationException)
        {
            ws.Dispose();
            if (LastCertificateThumbprint != null && !LastCertificateWasValid && !ThumbprintMatchesPin())
                throw new CertificateNotTrustedException(LastCertificateThumbprint, LastCertificateSubject ?? "");
            throw new TrueNasConnectionException($"Das NAS unter {Uri.Host} ist nicht erreichbar.", ex);
        }

        _ws = ws;
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(ws, _loopCts.Token));
        _keepAlive = new Timer(_ => _ = PingAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert == null) return false;
        using var c2 = new X509Certificate2(cert);
        LastCertificateThumbprint = FormatThumbprint(c2.GetCertHash(HashAlgorithmName.SHA256));
        LastCertificateSubject = c2.Subject;
        LastCertificateWasValid = errors == SslPolicyErrors.None;
        return LastCertificateWasValid || ThumbprintMatchesPin();
    }

    private bool ThumbprintMatchesPin() =>
        PinnedThumbprint != null && string.Equals(PinnedThumbprint, LastCertificateThumbprint, StringComparison.OrdinalIgnoreCase);

    private async Task PingAsync()
    {
        try { if (IsConnected) await CallAsync("core.ping", Array.Empty<object?>()); }
        catch { /* Verbindungsabbruch wird über Disconnected gemeldet */ }
    }

    public async Task<JsonNode?> CallAsync(string method, IReadOnlyList<object?> args, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open)
            throw new TrueNasConnectionException("Keine Verbindung zum NAS.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = new JsonArray(args.Select(ToNode).ToArray()),
        };
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());

        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new TrueNasConnectionException("Die Verbindung zum NAS wurde unterbrochen.", ex);
        }
        finally { _sendLock.Release(); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        using (timeout.Token.Register(() => tcs.TrySetCanceled()))
        {
            try { return await tcs.Task; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TrueNasConnectionException($"Das NAS hat nicht rechtzeitig geantwortet ({method}).");
            }
            finally { _pending.TryRemove(id, out _); }
        }
    }

    public async Task<JsonNode?> CallJobAsync(string method, IReadOnlyList<object?> args, IProgress<JobProgress>? progress = null, CancellationToken ct = default)
    {
        var jobIdNode = await CallAsync(method, args, ct);
        var jobId = jobIdNode!.GetValue<long>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var jobs = await CallAsync("core.get_jobs", new object?[] { new JsonArray(new JsonArray("id", "=", jobId)) }, ct);
            var job = jobs?.AsArray().FirstOrDefault()?.AsObject();
            if (job != null)
            {
                var state = job["state"]?.GetValue<string>();
                var p = job["progress"];
                if (p != null)
                    progress?.Report(new JobProgress(p["percent"]?.GetValue<double>() ?? 0, p["description"]?.GetValue<string>()));
                switch (state)
                {
                    case "SUCCESS": return job["result"]?.DeepClone();
                    case "FAILED":
                    case "ABORTED":
                        throw new TrueNasException(job["error"]?.GetValue<string>() ?? $"Der Vorgang {method} ist fehlgeschlagen.", method);
                }
            }
            await Task.Delay(400, ct);
        }
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n.Parent == null ? n : n.DeepClone(),
        _ => JsonSerializer.SerializeToNode(value),
    };

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Dispatch(text);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { return; }
        catch (Exception) { /* fällt durch zu Disconnected */ }

        FailAllPending(new TrueNasConnectionException("Die Verbindung zum NAS wurde getrennt."));
        if (!ct.IsCancellationRequested) Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void Dispatch(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); } catch (JsonException) { return; }
        if (node is not JsonObject obj || obj["id"] is not JsonValue idValue) return; // Benachrichtigungen ignorieren
        if (!idValue.TryGetValue<long>(out var id) || !_pending.TryRemove(id, out var tcs)) return;

        if (obj["error"] is JsonObject error)
            tcs.TrySetException(TrueNasException.FromRpcError(error));
        else
            tcs.TrySetResult(obj["result"]?.DeepClone());
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
    }

    private async Task CloseSocketAsync()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
        _loopCts?.Cancel();
        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
                }
            }
            catch { /* egal beim Schließen */ }
            _ws.Dispose();
            _ws = null;
        }
        FailAllPending(new TrueNasConnectionException("Verbindung geschlossen."));
    }

    public async ValueTask DisposeAsync() => await CloseSocketAsync();
}
