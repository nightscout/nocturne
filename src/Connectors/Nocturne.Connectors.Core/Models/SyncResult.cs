namespace Nocturne.Connectors.Core.Models;

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; set; }
    public Dictionary<SyncDataType, int> ItemsSynced { get; init; } = new();
    public List<string> Errors { get; init; } = [];

    /// <summary>
    ///     What the tenant has to do for the connector, or null when nothing. Read by the host after
    ///     every scheduled sync, which files or clears the tenant's notification accordingly; a
    ///     connector that never sets it never notifies.
    /// </summary>
    public ConnectorAttention? Attention { get; set; }
}