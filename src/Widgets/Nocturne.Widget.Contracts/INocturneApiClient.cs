namespace Nocturne.Widget.Contracts;

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
/// V4 summary response matching the server's Nocturne.Core.Models.Widget.V4SummaryResponse
/// </summary>
public class V4SummaryResponse
{
    public V4GlucoseReading? Current { get; set; }
    public List<V4GlucoseReading> History { get; set; } = [];
    public double Iob { get; set; }
    public double Cob { get; set; }
    public List<V4TrackerStatus> Trackers { get; set; } = [];
    public V4AlarmState? Alarm { get; set; }
    public V4Predictions? Predictions { get; set; }
    public long ServerMills { get; set; }
}

public class V4GlucoseReading
{
    public double Sgv { get; set; }
    public string? Direction { get; set; }
    public double? TrendRate { get; set; }
    public double? Delta { get; set; }
    public long Mills { get; set; }
    public int? Noise { get; set; }
}

public class V4TrackerStatus
{
    public string Id { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Category { get; set; }
    public string? Mode { get; set; }
    public double? AgeHours { get; set; }
    public double? HoursUntilEvent { get; set; }
    public string? Urgency { get; set; }
    public double? LifespanHours { get; set; }
    public double? PercentElapsed { get; set; }
}

public class V4AlarmState
{
    public int Level { get; set; }
    public string? Type { get; set; }
    public string? Message { get; set; }
    public long TriggeredMills { get; set; }
    public bool IsSilenced { get; set; }
    public long? SilenceExpiresMills { get; set; }
}

public class V4Predictions
{
    public List<double>? Values { get; set; }
    public long StartMills { get; set; }
    public long IntervalMills { get; set; }
    public string? Source { get; set; }
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
