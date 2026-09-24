namespace Nocturne.Connectors.Core.Models;

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; set; }
    public Dictionary<SyncDataType, int> ItemsSynced { get; init; } = new();

    /// <summary>
    /// Records the run fetched but did not write because the user had deleted them. Kept apart from
    /// <see cref="ItemsSynced"/>, which counts what reached the publisher rather than what it wrote.
    /// </summary>
    public int ItemsSkipped { get; set; }
    public List<string> Errors { get; init; } = [];
}