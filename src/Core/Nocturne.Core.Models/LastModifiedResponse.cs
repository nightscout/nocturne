namespace Nocturne.Core.Models;

/// <summary>
/// Last modified timestamps response model for the <c>/api/v3/lastModified</c> endpoint.
/// Provides timestamps for when each collection was last modified, enabling efficient
/// incremental sync by clients that cache data locally.
/// </summary>
/// <seealso cref="V3StatusResponse"/>
public class LastModifiedResponse
{
    public DateTime? Entries { get; set; }

    public DateTime? Treatments { get; set; }

    public DateTime? Profile { get; set; }

    /// <summary>
    /// Last modified timestamp for devicestatus collection
    /// </summary>
    public DateTime? DeviceStatus { get; set; }

    public DateTime? Food { get; set; }

    public DateTime? Settings { get; set; }

    public DateTime? Activity { get; set; }

    public DateTime ServerTime { get; set; }

    /// <summary>
    /// Additional collection timestamps
    /// For future extensibility and custom collections
    /// </summary>
    public Dictionary<string, DateTime> Additional { get; set; } = new();
}
