using System.Threading.Channels;

namespace Sse.Server;

/// <summary>A change event pushed to clients. Seq is a global monotonic id. Online carries liveness.</summary>
public record SensorEvent(long Seq, string SensorId, int Value, bool Online, bool Acknowledged, long Ts);

/// <summary>Current state of one sensor, used to seed a newly connected client.</summary>
public record SensorSnapshotItem(string SensorId, int Value, bool Online, bool Acknowledged, long Ts);

/// <summary>Full current state at a point in the seq stream. Client starts live tracking from Seq.</summary>
public record Snapshot(long Seq, IReadOnlyList<SensorSnapshotItem> Sensors);

public record HubStats(long Emitted, long Skipped, int Subscribers, int Total, int Online, int Offline);

/// <summary>
/// Central change tracker + fan-out for many sensors.
/// - Skips readings whose value is unchanged (D7), but still refreshes liveness.
/// - Marks a sensor offline when it stops reporting; emits online/offline transitions.
/// - Assigns a monotonic, contiguous seq to every emitted event (D8).
/// - Seeds new clients with a full snapshot; resumes reconnecting clients via history (D9).
/// </summary>
public sealed class SensorHub
{
    private const int MaxHistory = 50_000;

    private sealed class State
    {
        public int Value;
        public bool Online;
        public bool Acknowledged;   // operator has ack'd this sensor (client action -> server state)
        public long LastSeenTs;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new();
    private readonly List<SensorEvent> _history = new();
    private readonly List<Channel<SensorEvent>> _subscribers = new();

    private long _seq;
    private long _skipped;
    private long _outageUntilMs;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public HubStats Stats()
    {
        lock (_gate)
        {
            var online = 0;
            foreach (var s in _states.Values) if (s.Online) online++;
            return new HubStats(_seq, _skipped, _subscribers.Count, _states.Count, online, _states.Count - online);
        }
    }

    /// <summary>Publish a raw reading. Refreshes liveness; emits if value changed or the sensor just came online.</summary>
    public void Publish(string sensorId, int value)
    {
        SensorEvent evt;
        Channel<SensorEvent>[] subs;

        lock (_gate)
        {
            var now = Now();
            _states.TryGetValue(sensorId, out var s);
            var isNew = s is null;
            s ??= new State();

            var cameOnline = !s.Online;              // was offline or brand new
            var valueChanged = isNew || s.Value != value;

            s.Value = value;
            s.Online = true;
            s.LastSeenTs = now;
            if (cameOnline) s.Acknowledged = false;   // recovered -> clear any acknowledgment
            if (isNew) _states[sensorId] = s;

            if (!valueChanged && !cameOnline)         // nothing to report, but liveness refreshed above (D7)
            {
                _skipped++;
                return;
            }

            evt = Append(sensorId, value, true, s.Acknowledged, now);
            subs = _subscribers.ToArray();
        }

        Fan(subs, evt);
    }

    /// <summary>Mark sensors offline that have not reported within the window; emit one event each.</summary>
    public void Sweep(long offlineAfterMs)
    {
        List<SensorEvent>? events = null;
        Channel<SensorEvent>[] subs;

        lock (_gate)
        {
            var now = Now();
            foreach (var (id, s) in _states)
            {
                if (s.Online && now - s.LastSeenTs > offlineAfterMs)
                {
                    s.Online = false;
                    (events ??= new()).Add(Append(id, s.Value, false, s.Acknowledged, now));
                }
            }
            if (events is null) return;
            subs = _subscribers.ToArray();
        }

        foreach (var e in events) Fan(subs, e);
    }

    /// <summary>
    /// Subscribe for live events. New clients (lastEventId &lt;= 0) get a full snapshot;
    /// reconnecting clients get the backlog of missed events. Snapshot/backlog and subscriber
    /// registration happen under one lock, so nothing is missed or duplicated across the seam.
    /// </summary>
    public (Snapshot? Snapshot, IReadOnlyList<SensorEvent> Backlog, ChannelReader<SensorEvent> Reader, IDisposable Subscription) Subscribe(long lastEventId)
    {
        var channel = Channel.CreateUnbounded<SensorEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        Snapshot? snapshot = null;
        SensorEvent[] backlog = [];

        lock (_gate)
        {
            // Resume via backlog only if every event after lastEventId is still buffered.
            // Otherwise (new client, aged-out resume point, or server restart where seq reset)
            // re-seed with a full snapshot so the client is never left with a gap or stale state.
            var canResume = lastEventId > 0
                && _history.Count > 0
                && lastEventId <= _seq
                && lastEventId >= _history[0].Seq - 1;

            if (canResume)
            {
                backlog = _history.Where(e => e.Seq > lastEventId).ToArray();
            }
            else
            {
                var items = _states
                    .Select(kv => new SensorSnapshotItem(kv.Key, kv.Value.Value, kv.Value.Online, kv.Value.Acknowledged, kv.Value.LastSeenTs))
                    .OrderBy(i => i.SensorId, StringComparer.Ordinal)
                    .ToArray();
                snapshot = new Snapshot(_seq, items);
            }
            _subscribers.Add(channel);
        }

        return (snapshot, backlog, channel.Reader, new Subscription(this, channel));
    }

    // Must be called under _gate.
    private SensorEvent Append(string sensorId, int value, bool online, bool acknowledged, long ts)
    {
        var evt = new SensorEvent(++_seq, sensorId, value, online, acknowledged, ts);
        _history.Add(evt);
        if (_history.Count > MaxHistory)
            _history.RemoveRange(0, _history.Count - MaxHistory);
        return evt;
    }

    private static void Fan(Channel<SensorEvent>[] subs, SensorEvent evt)
    {
        foreach (var ch in subs) ch.Writer.TryWrite(evt);
    }

    // --- Client action -> state change -> broadcast -------------------------

    /// <summary>
    /// Toggle a sensor's acknowledged flag (a UI action) and broadcast the new state to every
    /// connected client. Returns false if the sensor is unknown.
    /// </summary>
    public bool ToggleAck(string sensorId)
    {
        SensorEvent evt;
        Channel<SensorEvent>[] subs;

        lock (_gate)
        {
            if (!_states.TryGetValue(sensorId, out var s)) return false;
            s.Acknowledged = !s.Acknowledged;
            evt = Append(sensorId, s.Value, s.Online, s.Acknowledged, Now());
            subs = _subscribers.ToArray();
        }

        Fan(subs, evt);
        return true;
    }

    // --- Chaos: simulate signal loss / downtime -----------------------------

    /// <summary>Force-disconnect every current subscriber (simulated signal loss). Clients reconnect and resume.</summary>
    public int DropAll()
    {
        Channel<SensorEvent>[] subs;
        lock (_gate) { subs = _subscribers.ToArray(); _subscribers.Clear(); }
        foreach (var ch in subs) ch.Writer.TryComplete();   // ends each reader loop -> connection closes
        return subs.Length;
    }

    /// <summary>Refuse new /sse connections for a window and drop existing ones (simulated downtime).</summary>
    public void StartOutage(int seconds)
    {
        lock (_gate) _outageUntilMs = Now() + seconds * 1000L;
        DropAll();
    }

    public bool IsOutage(out int retryAfterSec)
    {
        long until;
        lock (_gate) until = _outageUntilMs;
        var remaining = until - Now();
        retryAfterSec = remaining > 0 ? (int)Math.Ceiling(remaining / 1000.0) : 0;
        return remaining > 0;
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
