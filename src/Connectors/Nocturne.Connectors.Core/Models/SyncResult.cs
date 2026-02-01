namespace Nocturne.Connectors.Core.Models;

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public Dictionary<SyncDataType, int> ItemsSynced { get; set; } = new();
    public Dictionary<SyncDataType, DateTime?> LastEntryTimes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}