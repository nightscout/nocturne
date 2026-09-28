namespace Nocturne.Connectors.Core.Models;

public class SyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Set when the run was refused because another run of the same connector and tenant was
    /// already in flight. Distinct from <see cref="Success"/> being false, which reports a run
    /// that was attempted and failed.
    /// </summary>
    public bool AlreadyRunning { get; set; }
    public Dictionary<SyncDataType, int> ItemsSynced { get; init; } = new();

    /// <summary>
    /// Records the run fetched but did not write because the user had deleted them. Kept apart from
    /// <see cref="ItemsSynced"/>, which counts what reached the publisher rather than what it wrote.
    /// </summary>
    public int ItemsSkipped { get; set; }
    public List<string> Errors { get; init; } = [];
}