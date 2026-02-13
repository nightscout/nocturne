namespace Nocturne.Connectors.Core.Models;

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; set; }
    public Dictionary<SyncDataType, int> ItemsSynced { get; init; } = new();
    public Dictionary<SyncDataType, DateTime?> LastEntryTimes { get; init; } = new();
    public List<string> Errors { get; init; } = [];
}