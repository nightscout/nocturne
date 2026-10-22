namespace Nocturne.Core.Models;

/// <summary>
/// Direction information for glucose trend analysis, including the arrow symbol and label.
/// </summary>
/// <seealso cref="Direction"/>
/// <seealso cref="DeltaInfo"/>
public class DirectionInfo
{
    /// <summary>
    /// Direction string (e.g., "Flat", "SingleUp", "DoubleUp", etc.)
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>
    /// Trend label for display
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Trend arrow symbol
    /// </summary>
    public string Arrow { get; set; } = string.Empty;

    /// <summary>
    /// Display value (nullable for compatibility)
    /// </summary>
    public string? Display { get; set; }

    public Direction Value { get; set; }

    /// <summary>
    /// HTML entity for direction symbol
    /// </summary>
    public string Entity { get; set; } = string.Empty;
}

/// <summary>
/// Delta information for glucose changes between the current and previous readings.
/// </summary>
/// <seealso cref="DirectionInfo"/>
/// <seealso cref="Entry"/>
public class DeltaInfo
{
    public double Absolute { get; set; }

    public string Display { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether interpolation was used
    /// </summary>
    public bool Interpolated { get; set; }

    /// <summary>
    /// Mean 5 minutes ago value
    /// </summary>
    public double? Mean5MinsAgo { get; set; }

    /// <summary>
    /// Mean 15 minutes ago value
    /// </summary>
    public double? Mean15MinsAgo { get; set; }

    /// <summary>
    /// Elapsed minutes since last reading
    /// </summary>
    public double? ElapsedMins { get; set; }

    /// <summary>
    /// Delta in mg/dL units
    /// </summary>
    public double? Mgdl { get; set; }

    public double? Scaled { get; set; }

    public Dictionary<string, long> Times { get; set; } = new();
}

/// <summary>
/// Time pattern query for brace expansion
/// </summary>
public class TimePatternQuery
{
    /// <summary>
    /// Pattern string with braces
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    public List<TimeRange> Ranges { get; set; } = new();
}

public class TimeRange
{
    /// <summary>
    /// Start time in milliseconds
    /// </summary>
    public long Start { get; set; }

    /// <summary>
    /// End time in milliseconds
    /// </summary>
    public long End { get; set; }
}

public class TimeQueryEcho
{
    /// <summary>
    /// Request parameters that were processed
    /// </summary>
    public TimeQueryRequest Req { get; set; } = new();

    /// <summary>
    /// Generated patterns from brace expansion
    /// </summary>
    public IEnumerable<string> Pattern { get; set; } = Array.Empty<string>();

    /// <summary>
    /// MongoDB query that would be executed
    /// </summary>
    public Dictionary<string, object> Query { get; set; } = new();
}

/// <summary>
/// Request parameters for time query debugging
/// </summary>
public class TimeQueryRequest
{
    /// <summary>
    /// Route parameters
    /// </summary>
    public Dictionary<string, string?> Params { get; set; } = new();

    public Dictionary<string, object> Query { get; set; } = new();
}
