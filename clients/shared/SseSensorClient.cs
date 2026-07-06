using System.Text.Json;

namespace Crw.ClientShared;

/// <summary>Lets a host tweak the outgoing request (e.g. WASM must enable response streaming).</summary>
public delegate void SseRequestConfigurator(HttpRequestMessage request);

/// <summary>
/// Host-agnostic SSE consumer. Connects to /sse, parses the snapshot + live sensor frames,
/// and reconnects with Last-Event-ID on failure. Shared verbatim by the browser (WASM) and
/// desktop (WPF) hosts — only the request configurator differs between them.
/// </summary>
public sealed class SseSensorClient(HttpClient http, SseRequestConfigurator? configure = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http => http;

    public Task FireBurstAsync(int count = 1000) => http.PostAsync($"/admin/burst?count={count}", null);

    /// <summary>UI action: toggle a sensor's acknowledged flag; the server broadcasts the change to all clients.</summary>
    public Task AckAsync(string sensorId) => http.PostAsync($"/sensors/{Uri.EscapeDataString(sensorId)}/ack", null);

    /// <param name="lastSeq">Provider for the resume point (client's highest seen seq).</param>
    public async Task RunAsync(
        Func<SnapshotMsg, Task> onSnapshot,
        Func<SensorMsg, Task> onSensor,
        Action<string> onStatus,
        Func<long> lastSeq,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRead(onSnapshot, onSensor, onStatus, lastSeq, ct);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                onStatus("reconnecting");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ConnectAndRead(
        Func<SnapshotMsg, Task> onSnapshot,
        Func<SensorMsg, Task> onSensor,
        Action<string> onStatus,
        Func<long> lastSeq,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sse");
        var resume = lastSeq();
        if (resume > 0) request.Headers.TryAddWithoutValidation("Last-Event-ID", resume.ToString());
        configure?.Invoke(request);   // WASM: enable browser response streaming; desktop: no-op

        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        onStatus("connected");

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? ev = null, data = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;                      // stream ended -> reconnect
            if (line.StartsWith("event: ")) ev = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
            else if (line.Length == 0 && data is not null)
            {
                if (ev == "snapshot")
                {
                    var snap = JsonSerializer.Deserialize<SnapshotMsg>(data, Json);
                    if (snap is not null) await onSnapshot(snap);
                }
                else
                {
                    var msg = JsonSerializer.Deserialize<SensorMsg>(data, Json);
                    if (msg is not null) await onSensor(msg);
                }
                ev = null; data = null;
            }
        }
    }
}
