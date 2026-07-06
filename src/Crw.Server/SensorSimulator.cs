namespace Crw.Server;

/// <summary>
/// Simulated in-server data source (SCRUM-5 D2).
/// Emits periodic sensor readings via a random walk. Some ticks re-send the same
/// value on purpose, so the hub's unchanged-skip (D7) is exercised end-to-end.
/// </summary>
public sealed class SensorSimulator(SensorHub hub, ILogger<SensorSimulator> log) : BackgroundService
{
    private const int SensorCount = 5;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rng = new Random();
        var values = new int[SensorCount];
        for (var i = 0; i < SensorCount; i++) values[i] = rng.Next(0, 100);

        log.LogInformation("SensorSimulator started: {Count} sensors every {Interval}ms", SensorCount, Interval.TotalMilliseconds);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var i = rng.Next(SensorCount);

            // ~30% of ticks re-publish the current value -> hub skips it (D7 demo).
            if (rng.NextDouble() >= 0.30)
                values[i] = Math.Clamp(values[i] + rng.Next(-5, 6), 0, 100);

            hub.Publish($"sensor-{i}", values[i]);
        }
    }
}
