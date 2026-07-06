using System.Threading.Channels;

namespace Crw.Server;

/// <summary>A single change event pushed to clients. Seq is a global monotonic id.</summary>
public record SensorEvent(long Seq, string SensorId, int Value, long Ts);

/// <summary>
/// Central change tracker + fan-out.
/// - Skips readings whose value is unchanged (SCRUM-5 D7).
/// - Assigns a monotonic seq id to every emitted event (D8).
/// - Keeps a bounded history so reconnecting clients can resume via Last-Event-ID (D9).
/// </summary>
public sealed class SensorHub
{
    private const int MaxHistory = 20_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _lastSent = new();
    private readonly List<SensorEvent> _history = new();
    private readonly List<Channel<SensorEvent>> _subscribers = new();

    private long _seq;
    private long _skipped;

    public long Emitted { get { lock (_gate) return _seq; } }
    public long Skipped { get { lock (_gate) return _skipped; } }
    public int SubscriberCount { get { lock (_gate) return _subscribers.Count; } }

    /// <summary>Publish a raw reading. Emits only if the value changed for this sensor.</summary>
    public void Publish(string sensorId, int value)
    {
        SensorEvent evt;
        Channel<SensorEvent>[] subs;

        lock (_gate)
        {
            if (_lastSent.TryGetValue(sensorId, out var prev) && prev == value)
            {
                _skipped++;   // unchanged -> do not send (D7)
                return;
            }

            _lastSent[sensorId] = value;
            evt = new SensorEvent(++_seq, sensorId, value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            _history.Add(evt);
            if (_history.Count > MaxHistory)
                _history.RemoveRange(0, _history.Count - MaxHistory);

            subs = _subscribers.ToArray();
        }

        foreach (var ch in subs)
            ch.Writer.TryWrite(evt);   // unbounded writer never blocks
    }

    /// <summary>
    /// Subscribe for live events. Returns any missed events with Seq &gt; lastEventId first.
    /// Registration and history snapshot happen under one lock, so every event is delivered
    /// exactly once across (backlog, live).
    /// </summary>
    public (IReadOnlyList<SensorEvent> Backlog, ChannelReader<SensorEvent> Reader, IDisposable Subscription) Subscribe(long lastEventId)
    {
        var channel = Channel.CreateUnbounded<SensorEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        SensorEvent[] backlog;
        lock (_gate)
        {
            backlog = _history.Where(e => e.Seq > lastEventId).ToArray();
            _subscribers.Add(channel);
        }

        return (backlog, channel.Reader, new Subscription(this, channel));
    }

    private void Unsubscribe(Channel<SensorEvent> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private sealed class Subscription(SensorHub hub, Channel<SensorEvent> channel) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            hub.Unsubscribe(channel);
        }
    }
}
