using System.Text.Json;
using Crw.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// HTTP/2 so a browser is not capped at 6 connections per domain (SCRUM-5 D6).
// Over HTTPS Kestrel negotiates h2 via ALPN; over cleartext http it stays http/1.
builder.WebHost.ConfigureKestrel(o =>
    o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http1AndHttp2));

// Dev CORS so the React dev server (different origin) can open the stream.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<SensorHub>();
builder.Services.AddHostedService<SensorSimulator>();
builder.Services.AddHostedService<ChaosService>();

// Network latency simulation (off unless configured — see LatencyOptions / "Latency" config).
var latency = new LatencyOptions();
builder.Configuration.GetSection("Latency").Bind(latency);
builder.Services.AddSingleton(latency);

var app = builder.Build();

// Simulate request round-trip over a network: delay the start of every request.
if (latency.Enabled)
{
    app.Logger.LogInformation("Network latency simulation ON: base {Min}-{Max}ms, {Pct:P0} spikes {SMin}-{SMax}ms, stream jitter {Jit}ms",
        latency.BaseMinMs, latency.BaseMaxMs, latency.SpikeChance, latency.SpikeMinMs, latency.SpikeMaxMs, latency.StreamJitterMaxMs);
    app.Use(async (ctx, next) =>
    {
        var d = latency.NextRequestDelayMs();
        if (d > 0)
        {
            try { await Task.Delay(d, ctx.RequestAborted); }
            catch (OperationCanceledException) { return; }
        }
        await next();
    });
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// --- SSE stream -------------------------------------------------------------
app.MapGet("/sse", async (HttpContext ctx, SensorHub hub, LatencyOptions latency, CancellationToken ct) =>
{
    // Simulated downtime: refuse the connection so clients exercise their reconnect/backoff.
    if (hub.IsOutage(out var retryAfter))
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = retryAfter.ToString();
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // defeat proxy buffering

    // Resume point: browsers send Last-Event-ID on auto-reconnect; ?lastEventId= aids manual testing.
    long lastId = 0;
    if (ctx.Request.Headers.TryGetValue("Last-Event-ID", out var h) && long.TryParse(h, out var v)) lastId = v;
    else if (long.TryParse(ctx.Request.Query["lastEventId"], out var q)) lastId = q;

    var (snapshot, backlog, reader, subscription) = hub.Subscribe(lastId);
    using var _ = subscription;

    async Task Write(SensorEvent e)
    {
        var data = JsonSerializer.Serialize(e, json);
        await ctx.Response.WriteAsync($"id: {e.Seq}\nevent: sensor\ndata: {data}\n\n", ct);
    }

    // New client: seed the full current state so hundreds of sensors render at once.
    // No id: — the snapshot is not part of the seq stream; the client starts tracking from snapshot.seq.
    if (snapshot is not null)
    {
        var snap = JsonSerializer.Serialize(snapshot, json);
        await ctx.Response.WriteAsync($"event: snapshot\ndata: {snap}\n\n", ct);
    }
    foreach (var e in backlog) await Write(e);
    await ctx.Response.Body.FlushAsync(ct);

    try
    {
        while (!ct.IsCancellationRequested)
        {
            var ready = reader.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(15), ct));
            if (completed != ready)
            {
                await ctx.Response.WriteAsync(": keepalive\n\n", ct); // heartbeat
                await ctx.Response.Body.FlushAsync(ct);
                continue;
            }
            if (!await ready) break;
            // Hold the batch briefly before delivering (simulates in-flight time). One delay per
            // batch — events arriving during it coalesce, so latency stays bounded under load.
            var j = latency.NextStreamJitterMs();
            if (j > 0) await Task.Delay(j, ct);
            while (reader.TryRead(out var e)) await Write(e);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
});

// --- Test hooks -------------------------------------------------------------
// Burst load: strictly increasing values guarantee every one is a real change (SCRUM-5 load test).
app.MapPost("/admin/burst", (SensorHub hub, int count = 1000) =>
{
    var before = hub.Stats().Emitted;
    for (var n = 0; n < count; n++) hub.Publish("burst", n);
    return Results.Ok(new { requested = count, emitted = hub.Stats().Emitted - before });
});

app.MapGet("/admin/stats", (SensorHub hub) => Results.Ok(hub.Stats()));

// Chaos hooks: trigger resilience scenarios on demand.
app.MapPost("/admin/chaos/drop", (SensorHub hub) => Results.Ok(new { dropped = hub.DropAll() }));
app.MapPost("/admin/chaos/outage", (SensorHub hub, int seconds = 10) =>
{
    hub.StartOutage(seconds);
    return Results.Ok(new { outageSeconds = seconds });
});

app.Run();
