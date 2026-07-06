namespace Sse.Server;

/// <summary>
/// Opt-in chaos: randomly simulates signal loss (drops all connections) or downtime
/// (a short outage) so client reconnect + resume can be observed. Disabled by default;
/// enable with configuration "Chaos:Enabled" = true (env: Chaos__Enabled=true).
/// The /admin/chaos/* endpoints trigger the same effects on demand regardless of this flag.
/// </summary>
public sealed class ChaosService(SensorHub hub, IConfiguration config, ILogger<ChaosService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Chaos:Enabled", false))
            return;

        var min = config.GetValue("Chaos:MinIntervalSec", 20);
        var max = config.GetValue("Chaos:MaxIntervalSec", 45);
        var rng = new Random();

        log.LogWarning("ChaosService ENABLED: random drops/outages every {Min}-{Max}s", min, max);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(rng.Next(min, max + 1)), stoppingToken);

            if (rng.NextDouble() < 0.6)
            {
                var dropped = hub.DropAll();
                log.LogWarning("CHAOS: signal loss — dropped {Count} connection(s)", dropped);
            }
            else
            {
                var secs = rng.Next(3, 9);
                hub.StartOutage(secs);
                log.LogWarning("CHAOS: downtime — /sse outage for {Seconds}s", secs);
            }
        }
    }
}
