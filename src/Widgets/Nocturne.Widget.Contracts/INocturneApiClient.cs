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
/// V4 summary response for widget display
/// This is a placeholder that should be replaced with the actual generated model
/// when the V4 API endpoint is implemented
/// </summary>
public class V4SummaryResponse
{
    /// <summary>
    /// Gets or sets the current glucose value in mg/dL
    /// </summary>
    public int? CurrentGlucose { get; set; }

    /// <summary>
    /// Gets or sets the glucose trend direction
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>
    /// Gets or sets the glucose delta (rate of change)
    /// </summary>
    public double? Delta { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last reading in milliseconds
    /// </summary>
    public long? LastReadingMills { get; set; }

    /// <summary>
    /// Gets or sets the insulin on board in units
    /// </summary>
    public double? Iob { get; set; }

    /// <summary>
    /// Gets or sets the carbs on board in grams
    /// </summary>
    public int? Cob { get; set; }

    /// <summary>
    /// Gets or sets predicted glucose values
    /// </summary>
    public IList<PredictedGlucose>? Predictions { get; set; }
}

/// <summary>
/// Predicted glucose value
/// </summary>
public class PredictedGlucose
{
    /// <summary>
    /// Gets or sets the predicted glucose value in mg/dL
    /// </summary>
    public int Value { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the prediction in milliseconds
    /// </summary>
    public long Mills { get; set; }
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
