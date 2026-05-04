namespace Nocturne.Connectors.Core.Models;

public class SyncProgressEvent
{
    public required string ConnectorId { get; set; }
    public required string ConnectorName { get; set; }
    public SyncPhase Phase { get; set; }
    public SyncDataType? CurrentDataType { get; set; }
    public List<SyncDataType> CompletedDataTypes { get; set; } = [];
    public int TotalDataTypes { get; set; }
    public Dictionary<SyncDataType, int> ItemsSyncedSoFar { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public SyncMessageType? MessageType { get; set; }
    public Dictionary<string, string>? MessageParams { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
