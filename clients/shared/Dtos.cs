namespace Sse.ClientShared;

// Wire shapes shared by every client and matching the server's records.
public record SensorMsg(long Seq, string SensorId, int Value, bool Online, bool Acknowledged, long Ts);
public record SnapItem(string SensorId, int Value, bool Online, bool Acknowledged, long Ts);
public record SnapshotMsg(long Seq, List<SnapItem> Sensors);
