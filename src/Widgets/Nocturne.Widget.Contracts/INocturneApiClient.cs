using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;

namespace Nocturne.Widget.Contracts;

/// <summary>
/// Device ages returned by <c>/api/v4/deviceage/all</c>: cannula (CAGE), sensor (SAGE),
/// insulin reservoir (IAGE), and pump battery (BAGE).
/// </summary>
public class DeviceAgesResponse
{
    /// <summary>Cannula/infusion-site age.</summary>
    public DeviceAgeInfo? Cage { get; set; }

    /// <summary>Sensor age (start and change events).</summary>
    public SensorAgeInfo? Sage { get; set; }

    /// <summary>Insulin reservoir age.</summary>
    public DeviceAgeInfo? Iage { get; set; }

    /// <summary>Pump battery age.</summary>
    public DeviceAgeInfo? Bage { get; set; }
}

/// <summary>
/// Event arguments for data update events from the Nocturne API
/// </summary>
public class DataUpdateEventArgs : EventArgs
{
    /// <summary>
    /// Gets the timestamp of the update in milliseconds since Unix epoch
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Gets the type of data that was updated
    /// </summary>
    public string DataType { get; init; } = string.Empty;
}

/// <summary>
/// Event arguments for tracker update events
/// </summary>
public class TrackerUpdateEventArgs : EventArgs
{
    /// <summary>
    /// Gets the tracker identifier
    /// </summary>
    public string TrackerId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the tracker name
    /// </summary>
    public string TrackerName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current age in hours
    /// </summary>
    public double AgeHours { get; init; }

    /// <summary>
    /// Gets the expected lifespan in hours
    /// </summary>
    public double LifespanHours { get; init; }
}

/// <summary>
/// Event arguments for alarm events
/// </summary>
public class AlarmEventArgs : EventArgs
{
    /// <summary>
    /// Gets the alarm identifier
    /// </summary>
    public string AlarmId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the alarm title
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the alarm message
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the alarm level (0-4, higher is more severe)
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// Gets whether the alarm is urgent and requires immediate attention
    /// </summary>
    public bool Urgent { get; init; }

    /// <summary>
    /// Gets the timestamp when the alarm was triggered
    /// </summary>
    public long Timestamp { get; init; }
}

/// <summary>
/// Interface for the Nocturne API client providing HTTP and SignalR connectivity
/// </summary>
public interface INocturneApiClient
{
    /// <summary>
    /// Gets the summary data from the V4 API endpoint
    /// </summary>
    /// <param name="hours">Number of hours of data to include (0 for current only)</param>
    /// <param name="includePredictions">Whether to include glucose predictions</param>
    /// <returns>Summary response or null if unavailable</returns>
    Task<V4SummaryResponse?> GetSummaryAsync(int hours = 0, bool includePredictions = false);

    /// <summary>
    /// Gets the raw JSON body of the V4 summary endpoint, for republishing verbatim to the local
    /// glucose file the taskbar mod reads. Returning the unparsed body keeps it byte-for-byte
    /// identical to what the desktop companion writes (no enum/casing drift from re-serialization).
    /// </summary>
    /// <param name="hours">Number of hours of history to include (0 for current only)</param>
    /// <param name="includePredictions">Whether to include glucose predictions</param>
    /// <returns>The raw JSON response, or null if unavailable.</returns>
    Task<string?> GetSummaryRawAsync(int hours = 0, bool includePredictions = false);

    /// <summary>
    /// Gets multi-period glucose statistics (1/3/7/30/90 days) from the V4 statistics endpoint.
    /// The server caches the result, so frequent widget refreshes are cheap.
    /// </summary>
    /// <returns>Multi-period statistics, or null if unavailable.</returns>
    Task<MultiPeriodStatistics?> GetStatisticsAsync();

    /// <summary>
    /// Gets the current device ages (cannula, sensor, insulin, battery) from the V4 device-age endpoint.
    /// </summary>
    /// <returns>Device ages, or null if unavailable.</returns>
    Task<DeviceAgesResponse?> GetDeviceAgesAsync();

    /// <summary>
    /// Gets the most recent APS/loop algorithm snapshot for loop-status display.
    /// </summary>
    /// <returns>A single-item page with the latest snapshot, or null if unavailable.</returns>
    Task<PaginatedResponse<ApsSnapshot>?> GetLoopStatusAsync();

    /// <summary>
    /// Connects to the SignalR hub for real-time updates
    /// </summary>
    Task ConnectSignalRAsync();

    /// <summary>
    /// Disconnects from the SignalR hub
    /// </summary>
    Task DisconnectSignalRAsync();

    /// <summary>
    /// Raised when new data is available from the server
    /// </summary>
    event EventHandler<DataUpdateEventArgs>? DataUpdated;

    /// <summary>
    /// Raised when a tracker's state changes
    /// </summary>
    event EventHandler<TrackerUpdateEventArgs>? TrackerUpdated;

    /// <summary>
    /// Raised when an alarm is triggered
    /// </summary>
    event EventHandler<AlarmEventArgs>? AlarmReceived;

    /// <summary>
    /// Raised when an alarm is cleared/acknowledged
    /// </summary>
    event EventHandler<AlarmEventArgs>? AlarmCleared;
}
