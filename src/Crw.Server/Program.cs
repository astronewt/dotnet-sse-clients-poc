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

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// --- SSE stream -------------------------------------------------------------
app.MapGet("/sse", async (HttpContext ctx, SensorHub hub, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // defeat proxy buffering

    // Resume point: browsers send Last-Event-ID on auto-reconnect; ?lastEventId= aids manual testing.
    long lastId = 0;
    if (ctx.Request.Headers.TryGetValue("Last-Event-ID", out var h) && long.TryParse(h, out var v)) lastId = v;
    else if (long.TryParse(ctx.Request.Query["lastEventId"], out var q)) lastId = q;

    var (backlog, reader, subscription) = hub.Subscribe(lastId);
    using var _ = subscription;

    async Task Write(SensorEvent e)
    {
        var data = JsonSerializer.Serialize(e, json);
        await ctx.Response.WriteAsync($"id: {e.Seq}\nevent: sensor\ndata: {data}\n\n", ct);
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
    var before = hub.Emitted;
    for (var n = 0; n < count; n++) hub.Publish("burst", n);
    return Results.Ok(new { requested = count, emitted = hub.Emitted - before });
});

app.MapGet("/admin/stats", (SensorHub hub) =>
    Results.Ok(new { emitted = hub.Emitted, skipped = hub.Skipped, subscribers = hub.SubscriberCount }));

app.Run();
