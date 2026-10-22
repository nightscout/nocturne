using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Local analytics service that collects anonymous usage telemetry entirely in-process.
/// All counters are lock-free (<see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// and <see cref="System.Threading.Interlocked"/>) so tracking calls never block the request thread.
/// </summary>
/// <remarks>
/// <para>
/// The installation ID is persisted to <c>%APPDATA%/nocturne/installation_id</c> so the same
/// logical instance is recognised across restarts. If the file cannot be written (containerised
/// read-only filesystem, etc.) a session-scoped UUID v7 is used as a transient fallback.
/// </para>
/// <para>
/// Endpoint paths are normalised before storage: query strings are stripped, MongoDB ObjectIds
/// are replaced with <c>[id]</c>, GUIDs with <c>[guid]</c>, and numeric segments with <c>[id]</c>.
/// This prevents high-cardinality keys from flooding the dictionaries.
/// </para>
/// <para>
/// Events are enqueued to an in-memory <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>
/// and are never transmitted externally; <see cref="GetPendingAnalyticsDataAsync"/> surfaces them
/// for optional local consumption.
/// </para>
/// </remarks>
/// <seealso cref="IAnalyticsService"/>
public class AnalyticsService : IAnalyticsService
{
    private readonly ILogger<AnalyticsService> _logger;
    private readonly AnalyticsConfiguration _config;

    private readonly string _sessionId;
    private readonly string _installationId;
    private readonly SystemInfo _systemInfo;
    private readonly ConcurrentQueue<AnalyticsEvent> _pendingEvents;
    private readonly ConcurrentDictionary<string, long> _apiCallCounts;
    private readonly ConcurrentDictionary<string, long> _responseTimeTotals;
    private readonly ConcurrentDictionary<string, long> _pageViewCounts;
    private readonly ConcurrentDictionary<string, long> _featureUsageCounts;

    private AnalyticsCollectionConfig _collectionConfig;
    private readonly object _configLock = new();
    private DateTime _startTime;
    private long _totalRequests;
    private long _totalErrors;

    public AnalyticsService(
        ILogger<AnalyticsService> logger,
        IOptions<AnalyticsConfiguration> config
    )
    {
        _logger = logger;
        _config = config.Value;

        _sessionId = GenerateSessionId();
        _installationId = GetOrCreateInstallationId();
        _systemInfo = BuildSystemInfo();
        _pendingEvents = new ConcurrentQueue<AnalyticsEvent>();
        _apiCallCounts = new ConcurrentDictionary<string, long>();
        _responseTimeTotals = new ConcurrentDictionary<string, long>();
        _pageViewCounts = new ConcurrentDictionary<string, long>();
        _featureUsageCounts = new ConcurrentDictionary<string, long>();

        _collectionConfig = new AnalyticsCollectionConfig();
        _startTime = DateTime.UtcNow;

        if (_config.VerboseLogging)
        {
            _logger.LogInformation(
                "Analytics service initialized. Session ID: {SessionId}, Installation ID: {InstallationId}",
                _sessionId,
                _installationId
            );
        }
    }

    /// <summary>
    /// Records an API call. The endpoint is normalised to strip IDs and query strings before
    /// being used as a dictionary key. Calls to paths listed in
    /// <see cref="AnalyticsCollectionConfig.ExcludedEndpoints"/> are silently dropped.
    /// Status codes ≥ 400 increment the total error counter.
    /// </summary>
    public async Task TrackApiCallAsync(
        string endpoint,
        string method,
        long responseTime,
        int statusCode,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsAnalyticsEnabled() || !_collectionConfig.CollectApiUsage)
            return;

        // Check if endpoint should be excluded
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        if (
            _collectionConfig.ExcludedEndpoints.Any(excluded =>
                normalizedEndpoint.Contains(excluded, StringComparison.OrdinalIgnoreCase)
            )
        )
            return;

        Interlocked.Increment(ref _totalRequests);
        if (statusCode >= 400)
        {
            Interlocked.Increment(ref _totalErrors);
        }

        // Update running totals for performance metrics
        _apiCallCounts.AddOrUpdate(normalizedEndpoint, 1, (_, count) => count + 1);
        _responseTimeTotals.AddOrUpdate(
            normalizedEndpoint,
            responseTime,
            (_, total) => total + responseTime
        );

        var analyticsEvent = new AnalyticsEvent
        {
            SessionId = _sessionId,
            EventType = "api_call",
            Category = "api",
            Action = method,
            Label = normalizedEndpoint,
            Value = responseTime,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Metadata = new Dictionary<string, object>
            {
                ["status_code"] = statusCode,
                ["success"] = statusCode < 400,
            },
        };

        await EnqueueEventAsync(analyticsEvent, cancellationToken);
    }

    public async Task TrackPageViewAsync(
        string page,
        string? userAgent = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsAnalyticsEnabled() || !_collectionConfig.CollectUiUsage)
            return;

        _pageViewCounts.AddOrUpdate(page, 1, (_, count) => count + 1);

        var analyticsEvent = new AnalyticsEvent
        {
            SessionId = _sessionId,
            EventType = "page_view",
            Category = "ui",
            Action = "view",
            Label = page,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Metadata = new Dictionary<string, object>(),
        };

        // Extract device type from user agent (anonymized)
        if (!string.IsNullOrEmpty(userAgent))
        {
            analyticsEvent.Metadata["device_type"] = DetectDeviceType(userAgent);
        }

        await EnqueueEventAsync(analyticsEvent, cancellationToken);
    }

    public async Task TrackFeatureUsageAsync(
        string feature,
        string action,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsAnalyticsEnabled() || !_collectionConfig.CollectFeatureUsage)
            return;

        _featureUsageCounts.AddOrUpdate($"{feature}:{action}", 1, (_, count) => count + 1);

        var analyticsEvent = new AnalyticsEvent
        {
            SessionId = _sessionId,
            EventType = "feature_usage",
            Category = "feature",
            Action = action,
            Label = feature,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Metadata = metadata ?? new Dictionary<string, object>(),
        };

        await EnqueueEventAsync(analyticsEvent, cancellationToken);
    }

    public async Task TrackSystemEventAsync(
        string eventType,
        string message,
        string severity = "info",
        CancellationToken cancellationToken = default
    )
    {
        if (!IsAnalyticsEnabled() || !_collectionConfig.CollectHealthMetrics)
            return;

        var analyticsEvent = new AnalyticsEvent
        {
            SessionId = _sessionId,
            EventType = "system_event",
            Category = "system",
            Action = eventType,
            Label = message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Metadata = new Dictionary<string, object> { ["severity"] = severity },
        };

        await EnqueueEventAsync(analyticsEvent, cancellationToken);
    }

    public async Task TrackCustomEventAsync(
        AnalyticsEvent analyticsEvent,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsAnalyticsEnabled())
            return;

        // Ensure session ID is set
        analyticsEvent.SessionId = _sessionId;
        analyticsEvent.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await EnqueueEventAsync(analyticsEvent, cancellationToken);
    }

    public SystemInfo GetSystemInfo()
    {
        return _systemInfo;
    }

    /// <summary>
    /// Returns a snapshot of in-process performance counters. Average response time is computed
    /// across all tracked endpoints using the running totals accumulated since service start
    /// (or the last <see cref="ClearAnalyticsDataAsync"/> call).
    /// </summary>
    public PerformanceMetrics GetPerformanceMetrics()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var totalRequests = Interlocked.Read(ref _totalRequests);
        var totalErrors = Interlocked.Read(ref _totalErrors);

        // Calculate average response times
        var topEndpoints = _apiCallCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(10)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Calculate overall average response time
        var averageResponseTime = _responseTimeTotals.Any()
            ? _responseTimeTotals.Sum(kvp => kvp.Value) / (double)Math.Max(totalRequests, 1)
            : 0;

        return new PerformanceMetrics
        {
            AverageResponseTime = Math.Round(averageResponseTime, 2),
            TotalRequests = totalRequests,
            ErrorCount = totalErrors,
            MemoryUsageMB = GC.GetTotalMemory(false) / 1024 / 1024,
            UptimeHours = Math.Round(uptime.TotalHours, 2),
            TopEndpoints = topEndpoints,
        };
    }

    public UsageStatistics GetUsageStatistics()
    {
        var uptime = DateTime.UtcNow - _startTime;

        return new UsageStatistics
        {
            UniqueSessions = 1, // Current session
            PopularFeatures = _featureUsageCounts
                .OrderByDescending(kvp => kvp.Value)
                .Take(10)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            AverageSessionDuration = Math.Round(uptime.TotalMinutes, 2),
            DeviceTypes = _pageViewCounts.Any()
                ? new Dictionary<string, long> { ["web"] = _pageViewCounts.Sum(kvp => kvp.Value) }
                : new Dictionary<string, long>(),
        };
    }

    public bool IsAnalyticsEnabled()
    {
        return _config.Enabled;
    }

    public AnalyticsCollectionConfig GetAnalyticsConfig()
    {
        lock (_configLock)
        {
            return _collectionConfig;
        }
    }

    public Task UpdateAnalyticsConfigAsync(AnalyticsCollectionConfig config)
    {
        lock (_configLock)
        {
            _collectionConfig = config;
        }

        _logger.LogInformation("Analytics collection configuration updated");
        return Task.CompletedTask;
    }

    public Task<AnalyticsBatch?> GetPendingAnalyticsDataAsync()
    {
        if (_pendingEvents.IsEmpty)
            return Task.FromResult<AnalyticsBatch?>(null);

        var events = _pendingEvents.ToList();

        return Task.FromResult<AnalyticsBatch?>(
            new AnalyticsBatch
            {
                InstallationId = _installationId,
                Events = events,
                SystemInfo = _systemInfo,
                BatchTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SchemaVersion = "1.0",
            }
        );
    }

    public Task ClearAnalyticsDataAsync()
    {
        while (_pendingEvents.TryDequeue(out _)) { }

        _apiCallCounts.Clear();
        _responseTimeTotals.Clear();
        _pageViewCounts.Clear();
        _featureUsageCounts.Clear();

        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalErrors, 0);

        _logger.LogInformation("Analytics data cleared");
        return Task.CompletedTask;
    }

    private Task EnqueueEventAsync(
        AnalyticsEvent analyticsEvent,
        CancellationToken cancellationToken
    )
    {
        _pendingEvents.Enqueue(analyticsEvent);
        return Task.CompletedTask;
    }

    private static string GenerateSessionId()
    {
        return Guid.CreateVersion7().ToString("N")[..16];
    }

    private static string GetOrCreateInstallationId()
    {
        var installationFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "nocturne",
            "installation_id"
        );

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(installationFile)!);

            if (File.Exists(installationFile))
            {
                var existing = File.ReadAllText(installationFile).Trim();
                if (Guid.TryParse(existing, out _))
                {
                    return existing;
                }
            }

            var newId = Guid.CreateVersion7().ToString();
            File.WriteAllText(installationFile, newId);
            return newId;
        }
        catch
        {
            // Fallback to session-based ID if file operations fail
            return Guid.CreateVersion7().ToString();
        }
    }

    private SystemInfo BuildSystemInfo()
    {
        return new SystemInfo
        {
            Platform = RuntimeInformation.OSDescription,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            NocturneVersion = AppVersion.Current,
            DeploymentType = DetectDeploymentType(),
            DemoModeEnabled = false, // Will be set from configuration
            EnabledConnectors = new List<string>(), // Will be populated from configuration
            EnabledFeatures = new List<string>(), // Will be populated from configuration
            DatabaseType = "PostgreSQL",
            CacheEnabled = true, // Default assumption
        };
    }

    private static string DetectDeploymentType()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            return "Docker";

        if (Environment.GetEnvironmentVariable("ASPIRE_HOST") != null)
            return "Aspire";

        return "Standalone";
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        // Remove query parameters and normalize IDs
        var normalized = endpoint.Split('?')[0];

        // Replace common ID patterns with placeholders
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"/[0-9a-f]{24}",
            "/[id]"
        ); // MongoDB ObjectId
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            "/[guid]"
        ); // GUID
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"/\d+", "/[id]"); // Numeric IDs

        return normalized;
    }

    private static string DetectDeviceType(string userAgent)
    {
        var ua = userAgent.ToLowerInvariant();

        if (ua.Contains("mobile") || ua.Contains("android") || ua.Contains("iphone"))
            return "mobile";

        if (ua.Contains("tablet") || ua.Contains("ipad"))
            return "tablet";

        return "desktop";
    }
}
