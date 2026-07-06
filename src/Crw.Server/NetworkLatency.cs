namespace Crw.Server;

/// <summary>
/// Simulates that clients reach the server over a network, not localhost.
/// Adds a mostly-small delay to every request with occasional larger spikes, and an optional
/// per-event jitter on the SSE stream. Bound from the "Latency" configuration section.
/// Off by default (clean local dev); enabled in Docker where the topology is realistic.
/// </summary>
public sealed class LatencyOptions
{
    public bool Enabled { get; set; }

    // Regular request round-trip.
    public int BaseMinMs { get; set; } = 25;
    public int BaseMaxMs { get; set; } = 80;

    // Occasional spike (congestion, a slow hop, GC pause on a real box…).
    public double SpikeChance { get; set; } = 0.05;
    public int SpikeMinMs { get; set; } = 300;
    public int SpikeMaxMs { get; set; } = 1200;

    // Jitter added once per stream flush batch (0 = off). Bounded — does not accumulate under load.
    public int StreamJitterMaxMs { get; set; }

    /// <summary>Delay for one inbound request: usually BaseMin..BaseMax, sometimes a spike.</summary>
    public int NextRequestDelayMs()
    {
        if (!Enabled) return 0;
        return Random.Shared.NextDouble() < SpikeChance
            ? Random.Shared.Next(SpikeMinMs, SpikeMaxMs + 1)
            : Random.Shared.Next(BaseMinMs, BaseMaxMs + 1);
    }

    /// <summary>Jitter for one streamed event, or 0 when disabled.</summary>
    public int NextStreamJitterMs()
        => Enabled && StreamJitterMaxMs > 0 ? Random.Shared.Next(0, StreamJitterMaxMs + 1) : 0;
}
