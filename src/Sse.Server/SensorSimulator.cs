namespace Sse.Server;

/// <summary>
/// Simulated in-server data source (D2) scaled to hundreds of sensors.
/// - Every tick, each ONLINE sensor sends a heartbeat (keeps it online; the hub skips it as an
///   event unless the value changed).
/// - A few sensors periodically "go silent" (asleep): they stop reporting, so the hub's liveness
///   sweep flips them OFFLINE. When woken, they report again and come back ONLINE.
/// Counts and timings are configurable via the "Sensors" section of configuration.
/// </summary>
public sealed class SensorSimulator(SensorHub hub, IConfiguration config, ILogger<SensorSimulator> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var count = config.GetValue("Sensors:Count", 200);
        var tick = TimeSpan.FromMilliseconds(config.GetValue("Sensors:TickMs", 250));
        var offlineAfterMs = config.GetValue("Sensors:OfflineAfterMs", 4000);
        var changeChance = config.GetValue("Sensors:ChangeChance", 0.05);   // per online sensor per tick
        // Asymmetric so offline stays a realistic minority (~sleep/(sleep+wake) at equilibrium).
        var sleepChance = config.GetValue("Sensors:SleepChance", 0.01); // awake -> asleep, per sleep-cycle
        var wakeChance = config.GetValue("Sensors:WakeChance", 0.20);  // asleep -> awake, per sleep-cycle

        var rng = new Random();
        var values = new int[count];
        var asleep = new bool[count];
        for (var i = 0; i < count; i++) values[i] = rng.Next(0, 100);

        log.LogInformation("SensorSimulator started: {Count} sensors, tick {Tick}ms, offline after {Offline}ms",
            count, tick.TotalMilliseconds, offlineAfterMs);

        var n = 0;
        using var timer = new PeriodicTimer(tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            n++;

            // Every ~2s, a few awake sensors go silent and most silent ones wake up.
            if (n % 8 == 0)
                for (var i = 0; i < count; i++)
                    asleep[i] = asleep[i] ? rng.NextDouble() >= wakeChance : rng.NextDouble() < sleepChance;

            // Awake sensors report (heartbeat + occasional value change). Asleep ones stay silent.
            for (var i = 0; i < count; i++)
            {
                if (asleep[i]) continue;
                if (rng.NextDouble() < changeChance)
                    values[i] = Math.Clamp(values[i] + rng.Next(-5, 6), 0, 100);
                hub.Publish($"sensor-{i:D3}", values[i]);
            }

            // Liveness sweep every ~1s: silent sensors flip offline.
            if (n % 4 == 0)
                hub.Sweep(offlineAfterMs);
        }
    }
}
