namespace Nocturne.Core.Models;

/// <summary>
/// Overall compatibility metrics for dashboard, comparing Nightscout and Nocturne responses.
/// </summary>
/// <seealso cref="EndpointMetrics"/>
/// <seealso cref="DiscrepancyAnalysisDto"/>
public class CompatibilityMetrics
{
    public int TotalRequests { get; set; }

    /// <summary>Number of requests with identical responses.</summary>
    public int PerfectMatches { get; set; }

    public int MinorDifferences { get; set; }

    public int MajorDifferences { get; set; }

    public int CriticalDifferences { get; set; }

    /// <summary>Overall compatibility score (0-100).</summary>
    public double CompatibilityScore { get; set; }

    /// <summary>Average response time for the legacy Nightscout server (ms).</summary>
    public double AverageNightscoutResponseTime { get; set; }

    /// <summary>Average response time for Nocturne (ms).</summary>
    public double AverageNocturneResponseTime { get; set; }
}

/// <summary>
/// Per-endpoint compatibility metrics, breaking down <see cref="CompatibilityMetrics"/> by API path.
/// </summary>
/// <seealso cref="CompatibilityMetrics"/>
public class EndpointMetrics
{
    /// <summary>API endpoint path (e.g., "/api/v1/entries").</summary>
    public string Endpoint { get; set; } = string.Empty;

    public int TotalRequests { get; set; }

    /// <summary>Number of requests with identical responses.</summary>
    public int PerfectMatches { get; set; }

    public int MinorDifferences { get; set; }

    public int MajorDifferences { get; set; }

    public int CriticalDifferences { get; set; }

    /// <summary>Compatibility score for this endpoint (0-100).</summary>
    public double CompatibilityScore { get; set; }

    /// <summary>Average response time for the legacy Nightscout server (ms).</summary>
    public double AverageNightscoutResponseTime { get; set; }

    /// <summary>Average response time for Nocturne (ms).</summary>
    public double AverageNocturneResponseTime { get; set; }
}
