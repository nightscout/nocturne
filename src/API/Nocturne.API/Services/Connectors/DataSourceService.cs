using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Demo;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Domain service for querying and managing the data sources (connectors and direct Nightscout uploads)
/// connected to a Nocturne tenant. Aggregates connector metadata, last-seen timestamps from entries
/// and treatments, and enabled/disabled state for display in the admin UI.
/// </summary>
/// <seealso cref="IDataSourceService"/>
public class DataSourceService : IDataSourceService
{
    private readonly NocturneDbContext _context;
    private readonly ISensorGlucoseRepository _sensorGlucose;
    private readonly IMeterGlucoseRepository _meterGlucose;
    private readonly ICalibrationRepository _calibrations;
    private readonly IAuditContext _auditContext;
    private readonly IConnectorConfigurationService _connectorConfiguration;
    private readonly ILogger<DataSourceService> _logger;

    public DataSourceService(
        NocturneDbContext context,
        ISensorGlucoseRepository sensorGlucose,
        IMeterGlucoseRepository meterGlucose,
        ICalibrationRepository calibrations,
        IAuditContext auditContext,
        IConnectorConfigurationService connectorConfiguration,
        ILogger<DataSourceService> logger
    )
    {
        _context = context;
        _sensorGlucose = sensorGlucose;
        _meterGlucose = meterGlucose;
        _calibrations = calibrations;
        _auditContext = auditContext;
        _connectorConfiguration = connectorConfiguration;
        _logger = logger;
    }

    /// <param name="Handle">
    /// Which handle the bucket's key names, or <see langword="null"/> when no contributing table
    /// could tell.
    /// </param>
    private record TableStats(long Count, int CountLast24H, DateTime Latest, DateTime? Oldest, SourceHandle? Handle);

    private static void ApplyStatus(DataSourceInfo info, DateTimeOffset now, int activeMinutes, int staleMinutes)
    {
        var minutesSinceLast = info.LastSeen.HasValue
            ? (int)(now - info.LastSeen.Value).TotalMinutes
            : int.MaxValue;
        info.MinutesSinceLastData = minutesSinceLast;
        info.Status = minutesSinceLast switch
        {
            _ when minutesSinceLast < activeMinutes => "active",
            _ when minutesSinceLast < staleMinutes => "stale",
            _ => "inactive",
        };
    }

    private (int activeMinutes, int staleMinutes) ResolveThresholds(
        string? connectorDataSourceId,
        Dictionary<string, (int active, int stale)>? configOverrides)
    {
        const int defaultActive = 15;
        const int defaultStale = 60;

        if (connectorDataSourceId == null)
            return (defaultActive, defaultStale);

        if (configOverrides != null &&
            configOverrides.TryGetValue(connectorDataSourceId, out var overrides))
        {
            var active = overrides.active > 0 ? overrides.active : 0;
            var stale = overrides.stale > 0 ? overrides.stale : 0;

            if (active > 0 || stale > 0)
            {
                var meta = ConnectorMetadataService.GetByDataSourceId(connectorDataSourceId);
                return (
                    active > 0 ? active : meta?.DefaultActiveThresholdMinutes ?? defaultActive,
                    stale > 0 ? stale : meta?.DefaultStaleThresholdMinutes ?? defaultStale);
            }
        }

        var connectorMeta = ConnectorMetadataService.GetByDataSourceId(connectorDataSourceId);
        if (connectorMeta != null)
            return (connectorMeta.DefaultActiveThresholdMinutes, connectorMeta.DefaultStaleThresholdMinutes);

        return (defaultActive, defaultStale);
    }

    private async Task<Dictionary<string, (int active, int stale)>> LoadThresholdOverridesAsync(
        CancellationToken ct)
    {
        var configs = await _context.ConnectorConfigurations
            .AsNoTracking()
            .Select(c => new { c.ConnectorName, c.ConfigurationJson })
            .ToListAsync(ct);

        var result = new Dictionary<string, (int active, int stale)>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs)
        {
            var meta = ConnectorMetadataService.GetByConnectorId(config.ConnectorName);
            if (meta == null || string.IsNullOrEmpty(meta.DataSourceId)) continue;

            try
            {
                using var doc = JsonDocument.Parse(config.ConfigurationJson);
                var root = doc.RootElement;

                var active = root.TryGetProperty("activeThresholdMinutes", out var aProp) && aProp.TryGetInt32(out var a) ? a : 0;
                var stale = root.TryGetProperty("staleThresholdMinutes", out var sProp) && sProp.TryGetInt32(out var s) ? s : 0;

                result[meta.DataSourceId] = (active, stale);
            }
            catch
            {
                // Invalid JSON — skip
            }
        }

        return result;
    }

    /// <summary>
    /// Aggregates every non-glucose table a source can produce rows in, bucketed by the source
    /// identifier the row carries. The bucket key is <c>DataSource ?? Device</c>, the same key the
    /// discovery merge resolves an entry under, and each bucket records which handle produced it
    /// (<see cref="SourceHandle"/>) so an entry surfaced from a bucket alone can say so — or records
    /// that no contributing table could tell.
    /// </summary>
    private async Task<Dictionary<string, TableStats>> GetNonGlucoseStatsAsync(
        DateTime thirtyDaysAgo, DateTime last24HoursDate, CancellationToken ct)
    {
        var result = new Dictionary<string, TableStats>(StringComparer.OrdinalIgnoreCase);

        // A table that stores the two handles in separate columns can say which one the key came
        // from; one that stores a single undifferentiated origin cannot, and contributes null so the
        // bucket keeps whatever evidence the other tables carry. Only a row bearing an actual
        // DataSource column value proves the key is a data source, so that answer wins over Device.
        static SourceHandle? CombineHandles(SourceHandle? left, SourceHandle? right) =>
            left == SourceHandle.DataSource || right == SourceHandle.DataSource
                ? SourceHandle.DataSource
                : left ?? right;

        void Merge(string? key, long count, int count24h, DateTime latest, DateTime? oldest, SourceHandle? handle)
        {
            if (string.IsNullOrEmpty(key) || count == 0) return;
            if (result.TryGetValue(key, out var existing))
            {
                result[key] = new TableStats(
                    existing.Count + count,
                    existing.CountLast24H + count24h,
                    latest > existing.Latest ? latest : existing.Latest,
                    oldest.HasValue && (!existing.Oldest.HasValue || oldest.Value < existing.Oldest.Value)
                        ? oldest : existing.Oldest,
                    CombineHandles(existing.Handle, handle));
            }
            else
            {
                result[key] = new TableStats(count, count24h, latest, oldest, handle);
            }
        }

        static SourceHandle HandleOf(bool fromDataSource) =>
            fromDataSource ? SourceHandle.DataSource : SourceHandle.Device;

        async Task MergeTimeSeriesAsync<TEntity>() where TEntity : class, IV4TimeSeriesEntity
        {
            var stats = await _context.Set<TEntity>()
                .Where(e => e.Timestamp >= thirtyDaysAgo)
                .GroupBy(e => e.DataSource ?? e.Device)
                .Select(g => new
                {
                    Key = g.Key,
                    FromDataSource = g.Max(x => x.DataSource) != null,
                    Count = g.LongCount(),
                    Count24H = g.Count(x => x.Timestamp >= last24HoursDate),
                    Latest = g.Max(x => x.Timestamp),
                    Oldest = (DateTime?)g.Min(x => x.Timestamp),
                })
                .ToListAsync(ct);

            foreach (var s in stats)
                Merge(s.Key, s.Count, s.Count24H, s.Latest, s.Oldest, HandleOf(s.FromDataSource));
        }

        await MergeTimeSeriesAsync<MeterGlucoseEntity>();
        await MergeTimeSeriesAsync<BolusEntity>();
        await MergeTimeSeriesAsync<CarbIntakeEntity>();
        await MergeTimeSeriesAsync<BGCheckEntity>();
        await MergeTimeSeriesAsync<NoteEntity>();
        await MergeTimeSeriesAsync<DeviceEventEntity>();
        await MergeTimeSeriesAsync<BolusCalculationEntity>();
        await MergeTimeSeriesAsync<ApsSnapshotEntity>();

        // TempBasal is span-shaped, so it keys on StartTimestamp and stays off IV4TimeSeriesEntity.
        var tbStats = await _context.TempBasals
            .Where(t => t.StartTimestamp >= thirtyDaysAgo)
            .GroupBy(t => t.DataSource ?? t.Device)
            .Select(g => new { Key = g.Key, FromDataSource = g.Max(x => x.DataSource) != null, Count = g.LongCount(), Count24H = g.Count(x => x.StartTimestamp >= last24HoursDate), Latest = g.Max(x => x.StartTimestamp), Oldest = (DateTime?)g.Min(x => x.StartTimestamp) })
            .ToListAsync(ct);
        foreach (var s in tbStats) Merge(s.Key, s.Count, s.Count24H, s.Latest, s.Oldest, HandleOf(s.FromDataSource));

        // StateSpan records one undifferentiated origin: its writers populate Source from the
        // reported device string (DeviceStatusDecomposer) or from the row's data source, falling back
        // to the entering party (TreatmentDecomposer). It therefore names neither handle in
        // particular and contributes no evidence about which one its bucket's key is.
        var ssStats = await _context.StateSpans
            .Where(s => s.StartTimestamp >= thirtyDaysAgo)
            .GroupBy(s => s.Source)
            .Select(g => new { Key = g.Key, Count = g.LongCount(), Count24H = g.Count(x => x.StartTimestamp >= last24HoursDate), Latest = g.Max(x => x.StartTimestamp), Oldest = (DateTime?)g.Min(x => x.StartTimestamp) })
            .ToListAsync(ct);
        foreach (var s in ssStats) Merge(s.Key, s.Count, s.Count24H, s.Latest, s.Oldest, null);

        return result;
    }

    /// <inheritdoc />
    public async Task<List<DataSourceInfo>> GetActiveDataSourcesAsync(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting active data sources");

        var now = DateTimeOffset.UtcNow;
        var last24Hours = now.AddHours(-24);
        var thirtyDaysAgoDate = now.AddDays(-30).UtcDateTime;
        var last24HoursDate = last24Hours.UtcDateTime;

        // Get distinct devices from V4 sensor glucose in the last 30 days
        var entryDevices = await _context
            .SensorGlucose.Where(e => e.Timestamp >= thirtyDaysAgoDate && e.Device != null && e.Device != "")
            .GroupBy(e => e.Device)
            .Select(g => new
            {
                Device = g.Key!,
                DataSource = g.Max(e => e.DataSource),
                LastTimestamp = g.Max(e => e.Timestamp),
                FirstTimestamp = g.Min(e => e.Timestamp),
                TotalCount = g.LongCount(),
                Last24HCount = g.Count(e => e.Timestamp >= last24HoursDate),
            })
            // Sibling devices sharing a data source both resolve to its single non-glucose bucket,
            // and only the first of them claims it. Ordering by the group key makes which one that is
            // the same on every refresh instead of whatever the grouping happened to return.
            .OrderBy(d => d.Device)
            .ToListAsync(cancellationToken);

        // Also check APS snapshots for devices that might not have entries. Discovery keys on Device,
        // not DataSource: an entry's identity here is the uploader string that CreateDataSourceInfo
        // parses for name, category and icon, so a connector-imported snapshot still lists its rig.
        // The connector keeps its own entry via GetNonGlucoseStatsAsync, which keys on DataSource.
        var deviceStatusDevices = await _context
            .ApsSnapshots.Where(ds =>
                ds.Timestamp >= thirtyDaysAgoDate && ds.Device != null && ds.Device != ""
            )
            .GroupBy(ds => ds.Device)
            .Select(g => new
            {
                Device = g.Key!,
                DataSource = g.Max(ds => ds.DataSource),
                LastMills = new DateTimeOffset(g.Max(ds => ds.Timestamp), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            })
            .ToListAsync(cancellationToken);

        // Batch-aggregate non-glucose tables
        var nonGlucoseStats = await GetNonGlucoseStatsAsync(thirtyDaysAgoDate, last24HoursDate, cancellationToken);

        // Load user threshold overrides and connector configs for LastSuccessfulSync
        var thresholdOverrides = await LoadThresholdOverridesAsync(cancellationToken);
        var connectorConfigs = await _context.ConnectorConfigurations
            .AsNoTracking()
            .Select(c => new { c.ConnectorName, c.LastSuccessfulSync })
            .ToListAsync(cancellationToken);

        var dataSources = new List<DataSourceInfo>();
        var processedNonGlucoseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A bucket is consumed by exactly one entry, or by none and surfaced as its own entry below.
        // An entry claims the bucket its own key names first; the Device-field fallback may then take
        // only a bucket no entry claims that way, which is the same entry the leftover loop would
        // have built for it. Without that exclusivity a bucket reachable from two entries is added
        // to both and any consumer summing the list double-counts it.
        var primaryKeys = new HashSet<string>(
            entryDevices.Select(d => d.DataSource ?? d.Device), StringComparer.OrdinalIgnoreCase);

        TableStats? Claim(string key) =>
            nonGlucoseStats.TryGetValue(key, out var stats) && processedNonGlucoseKeys.Add(key)
                ? stats
                : null;

        static void MergeStats(DataSourceInfo info, TableStats stats)
        {
            info.TotalEntries += stats.Count;
            info.EntriesLast24Hours += stats.CountLast24H;

            var latest = new DateTimeOffset(stats.Latest, TimeSpan.Zero);
            if (latest > info.LastSeen)
                info.LastSeen = latest;

            if (stats.Oldest.HasValue)
            {
                var oldest = new DateTimeOffset(stats.Oldest.Value, TimeSpan.Zero);
                if (!info.FirstSeen.HasValue || oldest < info.FirstSeen)
                    info.FirstSeen = oldest;
            }
        }

        foreach (var device in entryDevices)
        {
            var info = CreateDataSourceInfo(device.Device, device.DataSource, SourceHandle.Device);
            info.LastSeen = new DateTimeOffset(device.LastTimestamp, TimeSpan.Zero);
            info.FirstSeen = new DateTimeOffset(device.FirstTimestamp, TimeSpan.Zero);
            info.TotalEntries = device.TotalCount;
            info.EntriesLast24Hours = device.Last24HCount;

            // Check if there's device status data
            var dsDevice = deviceStatusDevices.FirstOrDefault(d => d.Device == device.Device);
            var lastSeenMills = new DateTimeOffset(device.LastTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
            if (dsDevice != null && dsDevice.LastMills > lastSeenMills)
            {
                info.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(dsDevice.LastMills);
            }

            // Set ConnectorId if this is a connector data source
            var connectorKey = device.DataSource ?? device.Device;
            var connectorMeta = ConnectorMetadataService.GetByDataSourceId(connectorKey);
            if (connectorMeta != null)
            {
                info.ConnectorId = connectorMeta.ConnectorName.ToLowerInvariant();
                var connConfig = connectorConfigs.FirstOrDefault(c =>
                    c.ConnectorName.Equals(connectorMeta.ConnectorName, StringComparison.OrdinalIgnoreCase));
                if (connConfig?.LastSuccessfulSync != null)
                    info.LastSuccessfulSync = new DateTimeOffset(connConfig.LastSuccessfulSync.Value, TimeSpan.Zero);
            }

            // Merge non-glucose stats
            var mergeKey = device.DataSource ?? device.Device;
            if (Claim(mergeKey) is { } ngStats)
                MergeStats(info, ngStats);

            // Also try matching by Device field
            if (mergeKey != device.Device
                && !primaryKeys.Contains(device.Device)
                && Claim(device.Device) is { } ngDeviceStats)
                MergeStats(info, ngDeviceStats);

            // Apply status with resolved thresholds
            var (activeMinutes, staleMinutes) = ResolveThresholds(connectorKey, thresholdOverrides);
            ApplyStatus(info, now, activeMinutes, staleMinutes);

            dataSources.Add(info);
        }

        // Add any device status only devices
        foreach (var dsDevice in deviceStatusDevices)
        {
            if (!dataSources.Any(d => d.DeviceId == dsDevice.Device))
            {
                var info = CreateDataSourceInfo(dsDevice.Device, dsDevice.DataSource, SourceHandle.Device);
                info.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(dsDevice.LastMills);
                info.FirstSeen = info.LastSeen;
                info.TotalEntries = 0;
                info.EntriesLast24Hours = 0;

                // Merge non-glucose stats if available
                if (Claim(dsDevice.Device) is { } ngStats)
                    MergeStats(info, ngStats);

                var (activeMinutes, staleMinutes) = ResolveThresholds(dsDevice.Device, thresholdOverrides);
                ApplyStatus(info, now, activeMinutes, staleMinutes);

                dataSources.Add(info);
            }
        }

        // Add data sources found only in non-glucose tables
        foreach (var (key, stats) in nonGlucoseStats)
        {
            if (processedNonGlucoseKeys.Contains(key)) continue;

            var info = CreateDataSourceInfo(key, key, stats.Handle ?? SourceHandle.Unknown);
            info.LastSeen = new DateTimeOffset(stats.Latest, TimeSpan.Zero);
            info.FirstSeen = stats.Oldest.HasValue ? new DateTimeOffset(stats.Oldest.Value, TimeSpan.Zero) : info.LastSeen;
            info.TotalEntries = stats.Count;
            info.EntriesLast24Hours = stats.CountLast24H;

            var connectorMeta = ConnectorMetadataService.GetByDataSourceId(key);
            if (connectorMeta != null)
            {
                info.ConnectorId = connectorMeta.ConnectorName.ToLowerInvariant();
                var connConfig = connectorConfigs.FirstOrDefault(c =>
                    c.ConnectorName.Equals(connectorMeta.ConnectorName, StringComparison.OrdinalIgnoreCase));
                if (connConfig?.LastSuccessfulSync != null)
                    info.LastSuccessfulSync = new DateTimeOffset(connConfig.LastSuccessfulSync.Value, TimeSpan.Zero);
            }

            var (activeMinutes, staleMinutes) = ResolveThresholds(key, thresholdOverrides);
            ApplyStatus(info, now, activeMinutes, staleMinutes);

            dataSources.Add(info);
        }

        return dataSources.OrderByDescending(d => d.LastSeen).ToList();
    }

    /// <inheritdoc />
    public async Task<DataSourceInfo?> GetDataSourceInfoAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        var sources = await GetActiveDataSourcesAsync(cancellationToken);
        return sources.FirstOrDefault(s => s.DeviceId == deviceId || s.Id == deviceId);
    }

    /// <inheritdoc />
    public async Task<List<AvailableConnector>> GetAvailableConnectorsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var connectors = ConnectorMetadataService.GetAll()
            .Select(connector => new AvailableConnector
            {
                Id = connector.ConnectorName.ToLowerInvariant(),
                Name = connector.DisplayName,
                Category = connector.Category.ToString().ToLowerInvariant(),
                Description = connector.Description,
                Icon = connector.Icon,
                Available = true,
                RequiresServerConfig = true,
                DataSourceId = connector.DataSourceId,
                DocumentationUrl = GetConnectorDocumentationUrl(connector.ConnectorName),
                ConfigFields = null,
            })
            .OrderBy(connector => connector.Name)
            .ToList();

        // Check which connectors have actual saved configuration in the database
        var configuredNames = await _context.ConnectorConfigurations
            .AsNoTracking()
            .Select(c => c.ConnectorName.ToLower())
            .ToListAsync(cancellationToken);

        var configuredSet = new HashSet<string>(configuredNames, StringComparer.OrdinalIgnoreCase);

        foreach (var connector in connectors)
        {
            connector.IsConfigured = configuredSet.Contains(connector.Id ?? "");
        }

        return connectors;
    }

    private static string? GetConnectorDocumentationUrl(string connectorName)
    {
        return connectorName.ToLowerInvariant() switch
        {
            "dexcom" => UrlConstants.External.DocsDexcom,
            "librelinkup" => UrlConstants.External.DocsLibre,
            "glooko" => UrlConstants.External.DocsGlooko,
            _ => null,
        };
    }

    /// <inheritdoc />
    public ConnectorCapabilities? GetConnectorCapabilities(string connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
        {
            return null;
        }

        var registration = ConnectorMetadataService.GetRegistrationByConnectorId(connectorId);
        if (registration == null)
        {
            return null;
        }

        return new ConnectorCapabilities
        {
            SupportedDataTypes = registration.SupportedDataTypes
                ?.Select(type => type.ToString())
                .ToList()
                ?? new List<string>(),
            SupportsHistoricalSync = registration.SupportsHistoricalSync,
            MaxHistoricalDays = registration.MaxHistoricalDays > 0
                ? registration.MaxHistoricalDays
                : null,
            SupportsManualSync = registration.SupportsManualSync
        };
    }

    /// <inheritdoc />
    public List<UploaderApp> GetUploaderApps()
    {
        return new List<UploaderApp>
        {
            new()
            {
                Id = "xdrip",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Cgm,
                Icon = "xdrip",
                Url = "https://github.com/NightscoutFoundation/xDrip",
            },
            new()
            {
                Id = "spike",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.Cgm,
                Icon = "spike",
                Url = "https://spike-app.com",
            },
            new()
            {
                Id = "loop",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.AidSystem,
                Icon = "loop",
                Url = "https://loopkit.github.io/loopdocs/",
            },
            new()
            {
                Id = "aaps",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.AidSystem,
                Icon = "aaps",
                Url = "https://wiki.aaps.app",
            },
            new()
            {
                Id = "trio",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.AidSystem,
                Icon = "trio",
                Url = "https://triodocs.org",
            },
            new()
            {
                Id = "iaps",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.AidSystem,
                Icon = "iaps",
                Url = "https://iaps.readthedocs.io",
            },
            new()
            {
                Id = "nightscout-uploader",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Uploader,
                Icon = "nightscout",
                Url = "https://github.com/nightscout/android-uploader",
            },
            new()
            {
                Id = "xdrip4ios",
                Platform = UploaderPlatform.iOS,
                Category = UploaderCategory.Cgm,
                Icon = "xdrip4ios",
                Url = "https://github.com/JohanDegraworksve/xdripswift",
            },
            new()
            {
                Id = "juggluco",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Cgm,
                Icon = "juggluco",
                Url = "https://juggluco.nl",
            },
            new()
            {
                Id = "glucotracker",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Cgm,
                Icon = "glucotracker",
                Url = "https://glucotracker.app",
            },
            new()
            {
                Id = "prelude",
                Platform = UploaderPlatform.Android,
                Category = UploaderCategory.Uploader,
                Icon = "prelude",
            },
        };
    }

    /// <inheritdoc />
    public async Task<ServicesOverview> GetServicesOverviewAsync(
        string baseUrl,
        bool isAuthenticated,
        CancellationToken cancellationToken = default
    )
    {
        var dataSources = await GetActiveDataSourcesAsync(cancellationToken);

        return new ServicesOverview
        {
            ActiveDataSources = dataSources,
            AvailableConnectors = await GetAvailableConnectorsAsync(cancellationToken),
            UploaderApps = GetUploaderApps(),
            ApiEndpoint = new ApiEndpointInfo
            {
                BaseUrl = baseUrl,
                RequiresApiSecret = true,
                IsAuthenticated = isAuthenticated,
                EntriesEndpoint = "/api/v1/entries",
                TreatmentsEndpoint = "/api/v1/treatments",
                DeviceStatusEndpoint = "/api/v1/devicestatus",
            },
        };
    }

    /// <summary>
    /// Create a DataSourceInfo from a device identifier
    /// </summary>
    private DataSourceInfo CreateDataSourceInfo(string deviceId, string? dataSource, SourceHandle handle)
    {
        var info = new DataSourceInfo
        {
            Id = GenerateId(deviceId),
            DeviceId = deviceId,
            DeviceIdHandle = handle,
        };

        // Parse device identifier to determine type
        var lowerDevice = deviceId.ToLowerInvariant();

        // Detect source type and category
        if (lowerDevice.Contains("xdrip4ios") || lowerDevice.Contains("xdripswift"))
        {
            info.Name = "xDrip4iOS";
            info.SourceType = "xdrip4ios";
            info.Category = "cgm";
            info.Icon = "xdrip4ios";
            info.Description = ExtractDeviceDescription(deviceId, "xDrip4iOS on");
        }
        else if (lowerDevice.Contains("xdrip"))
        {
            info.Name = "xDrip+";
            info.SourceType = "xdrip";
            info.Category = "cgm";
            info.Icon = "xdrip";
            info.Description = ExtractDeviceDescription(deviceId, "xDrip+ on");
        }
        else if (lowerDevice.Contains("juggluco"))
        {
            info.Name = "Juggluco";
            info.SourceType = "juggluco";
            info.Category = "cgm";
            info.Icon = "juggluco";
            info.Description = ExtractDeviceDescription(deviceId, "Juggluco on");
        }
        else if (lowerDevice.Contains("glucotracker"))
        {
            info.Name = "GlucoTracker";
            info.SourceType = "glucotracker";
            info.Category = "cgm";
            info.Icon = "glucotracker";
            info.Description = ExtractDeviceDescription(deviceId, "GlucoTracker on");
        }
        else if (lowerDevice.Contains("spike"))
        {
            info.Name = "Spike";
            info.SourceType = "spike";
            info.Category = "cgm";
            info.Icon = "spike";
            info.Description = ExtractDeviceDescription(deviceId, "Spike");
        }
        else if (lowerDevice.Contains("loop") && !lowerDevice.Contains("openaps"))
        {
            info.Name = "Loop";
            info.SourceType = "loop";
            info.Category = "aid-system";
            info.Icon = "loop";
            info.Description = "Loop iOS AID System";
        }
        else if (lowerDevice.Contains("aaps") || lowerDevice.Contains("androidaps"))
        {
            info.Name = "AndroidAPS";
            info.SourceType = "aaps";
            info.Category = "aid-system";
            info.Icon = "aaps";
            info.Description = "AndroidAPS AID System";
        }
        else if (lowerDevice.Contains("openaps") || lowerDevice.Contains("oref"))
        {
            info.Name = "OpenAPS";
            info.SourceType = "openaps";
            info.Category = "aid-system";
            info.Icon = "openaps";
            info.Description = "OpenAPS AID System";
        }
        else if (lowerDevice.Contains("trio"))
        {
            info.Name = "Trio";
            info.SourceType = "trio";
            info.Category = "aid-system";
            info.Icon = "trio";
            info.Description = "Trio iOS AID System";
        }
        else if (lowerDevice.Contains("iaps"))
        {
            info.Name = "iAPS";
            info.SourceType = "iaps";
            info.Category = "aid-system";
            info.Icon = "iaps";
            info.Description = "iAPS iOS AID System";
        }
        else if (lowerDevice.Contains("dexcom"))
        {
            info.Name = "Dexcom";
            info.SourceType = "dexcom";
            info.Category = "cgm";
            info.Icon = "dexcom";
            info.Description = ExtractDeviceDescription(deviceId, "Dexcom CGM");
        }
        else if (lowerDevice.Contains("libre") || lowerDevice.Contains("freestyle"))
        {
            info.Name = "FreeStyle Libre";
            info.SourceType = "libre";
            info.Category = "cgm";
            info.Icon = "libre";
            info.Description = "FreeStyle Libre CGM";
        }
        else if (
            (lowerDevice.Contains("medtronic")
                || lowerDevice.Contains("minimed")
                || lowerDevice.Contains("carelink"))
            && ConnectorMetadataService.GetByDataSourceId(dataSource) != null
        )
        {
            info.Name = "Medtronic";
            info.SourceType = "medtronic";
            info.Category = "pump";
            info.Icon = "medtronic";
            info.Description = "Medtronic Pump/CGM";
        }
        else if (lowerDevice.Contains("omnipod"))
        {
            info.Name = "Omnipod";
            info.SourceType = "omnipod";
            info.Category = "pump";
            info.Icon = "omnipod";
            info.Description = "Omnipod Pump";
        }
        else if (lowerDevice.Contains("tandem") || lowerDevice.Contains("t:slim"))
        {
            info.Name = "Tandem";
            info.SourceType = "tandem";
            info.Category = "pump";
            info.Icon = "tandem";
            info.Description = "Tandem Pump";
        }
        // Check if this is data from a connector using centralized metadata
        else if (ConnectorMetadataService.GetByDataSourceId(dataSource) is { } connectorInfo)
        {
            info.Name = connectorInfo.ConnectorName;
            info.SourceType = connectorInfo.DataSourceId;
            info.Category = "connector";
            info.Icon = connectorInfo.Icon;
            info.Description = connectorInfo.Description;
        }
        else if (dataSource == DataSources.DemoService)
        {
            info.Name = "Demo Data";
            info.SourceType = "demo";
            info.Category = "demo";
            info.Icon = "demo";
            info.Description = "Simulated demo data";
        }
        else
        {
            // Unknown device - use the raw identifier
            info.Name = CleanDeviceName(deviceId);
            info.SourceType = "unknown";
            info.Category = "unknown";
            info.Icon = "device";
            info.Description = deviceId;
        }

        return info;
    }


    /// <inheritdoc />
    public async Task<ConnectorDataSummary> GetConnectorDataSummaryAsync(
        string connectorId,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting data summary for connector: {ConnectorId}", connectorId);

        // Resolve the connector metadata to find the correct data source ID
        var metadata = ConnectorMetadataService.GetByConnectorId(connectorId);
        if (metadata == null)
        {
            return new ConnectorDataSummary { ConnectorId = connectorId };
        }

        var deviceId = metadata.DataSourceId;
        var counts = new Dictionary<string, long>();

        // Glucose
        var sensorGlucoseCount = await _context
            .SensorGlucose.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (sensorGlucoseCount > 0) counts[nameof(SyncDataType.Glucose)] = sensorGlucoseCount;

        var meterGlucoseCount = await _context
            .MeterGlucose.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (meterGlucoseCount > 0) counts[nameof(SyncDataType.ManualBG)] = meterGlucoseCount;

        var calibrationsCount = await _context
            .Calibrations.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (calibrationsCount > 0) counts[nameof(SyncDataType.Calibrations)] = calibrationsCount;

        // Treatments
        var bolusCount = await _context
            .Boluses.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (bolusCount > 0) counts[nameof(SyncDataType.Boluses)] = bolusCount;

        var carbIntakeCount = await _context
            .CarbIntakes.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (carbIntakeCount > 0) counts[nameof(SyncDataType.CarbIntake)] = carbIntakeCount;

        var bgChecksCount = await _context
            .BGChecks.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (bgChecksCount > 0) counts[nameof(SyncDataType.BGChecks)] = bgChecksCount;

        var bolusCalcCount = await _context
            .BolusCalculations.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (bolusCalcCount > 0) counts[nameof(SyncDataType.BolusCalculations)] = bolusCalcCount;

        var notesCount = await _context
            .Notes.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (notesCount > 0) counts[nameof(SyncDataType.Notes)] = notesCount;

        var deviceEventsCount = await _context
            .DeviceEvents.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (deviceEventsCount > 0) counts[nameof(SyncDataType.DeviceEvents)] = deviceEventsCount;

        var stateSpansCount = await _context
            .StateSpans.Where(s => s.Source == deviceId)
            .LongCountAsync(cancellationToken);
        if (stateSpansCount > 0) counts[nameof(SyncDataType.StateSpans)] = stateSpansCount;

        var deviceStatusCount = await _context
            .ApsSnapshots.FromSource(deviceId)
            .LongCountAsync(cancellationToken);
        if (deviceStatusCount > 0) counts[nameof(SyncDataType.DeviceStatus)] = deviceStatusCount;

        return new ConnectorDataSummary
        {
            ConnectorId = connectorId,
            RecordCounts = counts,
        };
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteConnectorDataAsync(
        string connectorId,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Deleting data for connector: {ConnectorId}", connectorId);

        try
        {
            // Resolve the connector metadata to find the correct data source ID
            var metadata = ConnectorMetadataService.GetByConnectorId(connectorId);
            if (metadata == null)
            {
                return DataSourceDeleteResult.Failed(
                    connectorId,
                    DataSourceDeleteError.NotFound,
                    $"Connector not found: {connectorId}");
            }

            // Connector's DataSourceId is what we use in the database (e.g. "dexcom-connector")
            // This is also what the connector uses as the Device field when writing entries
            var deviceId = metadata.DataSourceId;
            _logger.LogInformation(
                "Resolved connector {ConnectorId} to device ID {DeviceId}",
                connectorId,
                deviceId
            );

            // Disable the connector before purging so a scheduled or in-flight sync cannot
            // re-import the data we are about to delete.
            await _connectorConfiguration.SetActiveAsync(
                connectorId, isActive: false, _auditContext.SubjectName, cancellationToken);

            var deletedCounts = await DeleteAllSourceDataAsync(deviceId, cancellationToken);

            _logger.LogInformation(
                "Deleted data for connector {ConnectorId} (device {DeviceId}): {DeletedCounts}",
                connectorId,
                deviceId,
                string.Join(", ", deletedCounts.Select(kv => $"{kv.Value} {kv.Key}"))
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = deviceId,
                DeletedCounts = deletedCounts,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting data for connector: {ConnectorId}", connectorId);
            return DataSourceDeleteResult.Failed(
                connectorId,
                DataSourceDeleteError.DeleteFailed,
                "Failed to delete connector data");
        }
    }

    /// <summary>
    /// Deletes every data type attributable to <paramref name="source"/> (<see cref="SourceFilter"/>),
    /// routing each entity through the strongest audited path it supports so the deletes are
    /// attributed to the current request and the soft-delete dedup keeps them from re-importing —
    /// the same way glucose already behaves. Returns the per-type deleted counts (non-zero only).
    /// </summary>
    /// <remarks>
    /// Capability matrix (entity interfaces decide the path):
    /// <list type="bullet">
    /// <item>Glucose and the auditable+soft-deletable records (Bolus, CarbIntake, BolusCalculation,
    ///   DeviceEvent, BGCheck, Note, ApsSnapshot, TempBasal, StateSpan) take the audited soft-delete path,
    ///   which stamps the <c>deleted_by_user</c> flag the soft-delete dedup reads to block re-import
    ///   (<see cref="SoftDeleteDedupExtensions"/>).</item>
    /// </list>
    /// </remarks>
    private async Task<Dictionary<string, long>> DeleteAllSourceDataAsync(
        string source,
        CancellationToken cancellationToken
    )
    {
        // Glucose: audited soft-delete, user-attributed (via the V4 repositories).
        var sensorGlucoseDeleted = await _sensorGlucose.DeleteBySourceAsync(source, cancellationToken);
        var meterGlucoseDeleted = await _meterGlucose.DeleteBySourceAsync(source, cancellationToken);
        var calibrationsDeleted = await _calibrations.DeleteBySourceAsync(source, cancellationToken);

        // Auditable + soft-deletable records: audited soft-delete, user-attributed.
        var scope = $"data_source={source}";
        var bolusesDeleted = await _context.AuditedSoftDeleteAsync(
            _context.Boluses.FromSource(source), _auditContext, scope, cancellationToken);
        var carbIntakesDeleted = await _context.AuditedSoftDeleteAsync(
            _context.CarbIntakes.FromSource(source), _auditContext, scope, cancellationToken);
        var bolusCalcsDeleted = await _context.AuditedSoftDeleteAsync(
            _context.BolusCalculations.FromSource(source), _auditContext, scope, cancellationToken);
        var deviceEventsDeleted = await _context.AuditedSoftDeleteAsync(
            _context.DeviceEvents.FromSource(source), _auditContext, scope, cancellationToken);
        var bgChecksDeleted = await _context.AuditedSoftDeleteAsync(
            _context.BGChecks.FromSource(source), _auditContext, scope, cancellationToken);
        var notesDeleted = await _context.AuditedSoftDeleteAsync(
            _context.Notes.FromSource(source), _auditContext, scope, cancellationToken);
        var deviceStatusDeleted = await _context.AuditedSoftDeleteAsync(
            _context.ApsSnapshots.FromSource(source), _auditContext, scope, cancellationToken);
        var tempBasalsDeleted = await _context.AuditedSoftDeleteAsync(
            _context.TempBasals.FromSource(source), _auditContext, scope, cancellationToken);

        var stateSpansDeleted = await _context.AuditedSoftDeleteAsync(
            _context.StateSpans.Where(s => s.Source == source), _auditContext, scope, cancellationToken);

        var deletedCounts = new Dictionary<string, long>();
        if (sensorGlucoseDeleted > 0) deletedCounts[nameof(SyncDataType.Glucose)] = sensorGlucoseDeleted;
        if (meterGlucoseDeleted > 0) deletedCounts[nameof(SyncDataType.ManualBG)] = meterGlucoseDeleted;
        if (calibrationsDeleted > 0) deletedCounts[nameof(SyncDataType.Calibrations)] = calibrationsDeleted;
        if (bolusesDeleted > 0) deletedCounts[nameof(SyncDataType.Boluses)] = bolusesDeleted;
        if (carbIntakesDeleted > 0) deletedCounts[nameof(SyncDataType.CarbIntake)] = carbIntakesDeleted;
        if (bgChecksDeleted > 0) deletedCounts[nameof(SyncDataType.BGChecks)] = bgChecksDeleted;
        if (bolusCalcsDeleted > 0) deletedCounts[nameof(SyncDataType.BolusCalculations)] = bolusCalcsDeleted;
        if (notesDeleted > 0) deletedCounts[nameof(SyncDataType.Notes)] = notesDeleted;
        if (deviceEventsDeleted > 0) deletedCounts[nameof(SyncDataType.DeviceEvents)] = deviceEventsDeleted;
        if (deviceStatusDeleted > 0) deletedCounts[nameof(SyncDataType.DeviceStatus)] = deviceStatusDeleted;
        if (tempBasalsDeleted > 0) deletedCounts[nameof(SyncDataType.TempBasals)] = tempBasalsDeleted;
        if (stateSpansDeleted > 0) deletedCounts[nameof(SyncDataType.StateSpans)] = stateSpansDeleted;

        return deletedCounts;
    }

    internal static string GenerateId(string deviceId) => $"ds-{HashUtils.Sha256Hex(deviceId)[..8]}";

    internal static string CleanDeviceName(string deviceId)
    {
        var name = deviceId.Replace("-", " ").Replace("_", " ").Trim();

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    private static string ExtractDeviceDescription(string deviceId, string prefix)
    {
        var parts = deviceId.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            return $"{prefix} ({string.Join(" ", parts.Skip(1))})";
        }
        return prefix;
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteDataSourceDataAsync(
        string dataSourceId,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Deleting data for data source: {DataSourceId}", dataSourceId);

        try
        {
            var sources = await GetActiveDataSourcesAsync(cancellationToken);
            var source = sources.FirstOrDefault(s =>
                s.Id == dataSourceId || s.DeviceId == dataSourceId
            );

            if (source == null)
            {
                _logger.LogWarning("Data source not found: {DataSourceId}", dataSourceId);
                return DataSourceDeleteResult.Failed(
                    dataSourceId,
                    DataSourceDeleteError.NotFound,
                    $"Data source not found: {dataSourceId}");
            }

            var deviceId = source.DeviceId;

            var deletedCounts = await DeleteAllSourceDataAsync(deviceId, cancellationToken);

            _logger.LogInformation(
                "Deleted data for {DeviceId}: {DeletedCounts}",
                deviceId,
                string.Join(", ", deletedCounts.Select(kv => $"{kv.Value} {kv.Key}"))
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = deviceId,
                DeletedCounts = deletedCounts,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error deleting data for data source: {DataSourceId}",
                dataSourceId
            );
            return DataSourceDeleteResult.Failed(
                dataSourceId,
                DataSourceDeleteError.DeleteFailed,
                "Failed to delete data source data");
        }
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteDemoDataAsync(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Deleting all demo data");

        try
        {
            var glucoseDeleted = await DemoDataPurge.PurgeEntriesAsync(_context, cancellationToken);
            var treatmentsDeleted = await DemoDataPurge.PurgeTreatmentsAsync(_context, cancellationToken);
            var deviceStatusDeleted = await DemoDataPurge.PurgeDeviceStatusAsync(_context, cancellationToken);

            var deletedCounts = new Dictionary<string, long>();
            if (glucoseDeleted > 0) deletedCounts[nameof(SyncDataType.Glucose)] = glucoseDeleted;
            if (treatmentsDeleted > 0) deletedCounts["Treatments"] = treatmentsDeleted;
            if (deviceStatusDeleted > 0) deletedCounts[nameof(SyncDataType.DeviceStatus)] = deviceStatusDeleted;

            _logger.LogInformation(
                "Deleted demo data: {DeletedCounts}",
                string.Join(", ", deletedCounts.Select(kv => $"{kv.Value} {kv.Key}"))
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = DataSources.DemoService,
                DeletedCounts = deletedCounts,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting demo data");
            return DataSourceDeleteResult.Failed(
                DataSources.DemoService,
                DataSourceDeleteError.DeleteFailed,
                "Failed to delete demo data");
        }
    }

    /// <inheritdoc />
    public async Task<DataSourceStats> GetDataSourceStatsAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTimeOffset.UtcNow;
        var oneDayAgoDate = now.AddHours(-24).UtcDateTime;

        // Query V4 sensor glucose stats
        var sgStats = await _context
            .SensorGlucose.FromSource(dataSource)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.LongCount(),
                Last24H = g.Count(sg => sg.Timestamp >= oneDayAgoDate),
                Latest = g.Max(sg => (DateTime?)sg.Timestamp),
                Oldest = g.Min(sg => (DateTime?)sg.Timestamp),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Query state span stats
        var stateSpanStats = await _context
            .StateSpans.Where(s => s.Source == dataSource)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalStateSpans = g.LongCount(),
                StateSpansLast24Hours = g.Count(s => s.StartTimestamp >= oneDayAgoDate),
                LastStateSpanTime = g.Max(s => (DateTime?)s.StartTimestamp),
                FirstStateSpanTime = g.Min(s => (DateTime?)s.StartTimestamp),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Query V4 table counts for per-type breakdown
        var meterGlucoseTotal = await _context
            .MeterGlucose.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var meterGlucose24h = meterGlucoseTotal > 0
            ? await _context.MeterGlucose
                .FromSource(dataSource)
                .Where(mg => mg.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bolusesTotal = await _context
            .Boluses.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var boluses24h = bolusesTotal > 0
            ? await _context.Boluses
                .FromSource(dataSource)
                .Where(b => b.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var carbIntakesTotal = await _context
            .CarbIntakes.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var carbIntakes24h = carbIntakesTotal > 0
            ? await _context.CarbIntakes
                .FromSource(dataSource)
                .Where(c => c.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bolusCalcsTotal = await _context
            .BolusCalculations.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var bolusCalcs24h = bolusCalcsTotal > 0
            ? await _context.BolusCalculations
                .FromSource(dataSource)
                .Where(bc => bc.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var notesTotal = await _context
            .Notes.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var notes24h = notesTotal > 0
            ? await _context.Notes
                .FromSource(dataSource)
                .Where(n => n.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var deviceEventsTotal = await _context
            .DeviceEvents.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var deviceEvents24h = deviceEventsTotal > 0
            ? await _context.DeviceEvents
                .FromSource(dataSource)
                .Where(de => de.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var deviceStatusTotal = await _context
            .ApsSnapshots.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var deviceStatus24h = deviceStatusTotal > 0
            ? await _context.ApsSnapshots
                .FromSource(dataSource)
                .Where(ds => ds.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bgChecksTotal = await _context
            .BGChecks.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var bgChecks24h = bgChecksTotal > 0
            ? await _context.BGChecks
                .FromSource(dataSource)
                .Where(b => b.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var tempBasalsTotal = await _context
            .TempBasals.FromSource(dataSource)
            .LongCountAsync(cancellationToken);
        var tempBasals24h = tempBasalsTotal > 0
            ? await _context.TempBasals
                .FromSource(dataSource)
                .Where(t => t.StartTimestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        // Build per-type breakdown dictionaries
        var typeBreakdown = new Dictionary<string, long>();
        var typeBreakdown24h = new Dictionary<string, int>();

        var glucoseTotal = sgStats?.Total ?? 0;
        var glucose24h = sgStats?.Last24H ?? 0;
        if (glucoseTotal > 0) { typeBreakdown[nameof(SyncDataType.Glucose)] = glucoseTotal; typeBreakdown24h[nameof(SyncDataType.Glucose)] = glucose24h; }
        if (meterGlucoseTotal > 0) { typeBreakdown[nameof(SyncDataType.ManualBG)] = meterGlucoseTotal; typeBreakdown24h[nameof(SyncDataType.ManualBG)] = meterGlucose24h; }
        if (bolusesTotal > 0) { typeBreakdown[nameof(SyncDataType.Boluses)] = bolusesTotal; typeBreakdown24h[nameof(SyncDataType.Boluses)] = boluses24h; }
        if (carbIntakesTotal > 0) { typeBreakdown[nameof(SyncDataType.CarbIntake)] = carbIntakesTotal; typeBreakdown24h[nameof(SyncDataType.CarbIntake)] = carbIntakes24h; }
        if (bolusCalcsTotal > 0) { typeBreakdown[nameof(SyncDataType.BolusCalculations)] = bolusCalcsTotal; typeBreakdown24h[nameof(SyncDataType.BolusCalculations)] = bolusCalcs24h; }
        if (notesTotal > 0) { typeBreakdown[nameof(SyncDataType.Notes)] = notesTotal; typeBreakdown24h[nameof(SyncDataType.Notes)] = notes24h; }
        if (deviceEventsTotal > 0) { typeBreakdown[nameof(SyncDataType.DeviceEvents)] = deviceEventsTotal; typeBreakdown24h[nameof(SyncDataType.DeviceEvents)] = deviceEvents24h; }
        if (bgChecksTotal > 0) { typeBreakdown[nameof(SyncDataType.BGChecks)] = bgChecksTotal; typeBreakdown24h[nameof(SyncDataType.BGChecks)] = bgChecks24h; }
        if (tempBasalsTotal > 0) { typeBreakdown[nameof(SyncDataType.TempBasals)] = tempBasalsTotal; typeBreakdown24h[nameof(SyncDataType.TempBasals)] = tempBasals24h; }
        if ((stateSpanStats?.TotalStateSpans ?? 0) > 0) { typeBreakdown[nameof(SyncDataType.StateSpans)] = stateSpanStats!.TotalStateSpans; typeBreakdown24h[nameof(SyncDataType.StateSpans)] = stateSpanStats.StateSpansLast24Hours; }
        if (deviceStatusTotal > 0) { typeBreakdown[nameof(SyncDataType.DeviceStatus)] = deviceStatusTotal; typeBreakdown24h[nameof(SyncDataType.DeviceStatus)] = deviceStatus24h; }

        // Treatment totals cover exactly the types the first/last treatment timestamps span, so a
        // source with only one of them cannot report zero treatments alongside a treatment time.
        var totalTreatments = bolusesTotal + carbIntakesTotal + bolusCalcsTotal + notesTotal
            + deviceEventsTotal + bgChecksTotal + tempBasalsTotal;
        var treatments24h = boluses24h + carbIntakes24h + bolusCalcs24h + notes24h
            + deviceEvents24h + bgChecks24h + tempBasals24h;
        var lastTreatmentTime = await GetLatestTreatmentTimestampBySourceAsync(dataSource, cancellationToken);
        var firstTreatmentTime = await GetOldestTreatmentTimestampBySourceAsync(dataSource, cancellationToken);

        return new DataSourceStats(
            dataSource,
            sgStats?.Total ?? 0,
            sgStats?.Last24H ?? 0,
            sgStats?.Latest,
            sgStats?.Oldest,
            totalTreatments,
            treatments24h,
            lastTreatmentTime,
            firstTreatmentTime,
            stateSpanStats?.TotalStateSpans ?? 0,
            stateSpanStats?.StateSpansLast24Hours ?? 0,
            stateSpanStats?.LastStateSpanTime,
            stateSpanStats?.FirstStateSpanTime,
            typeBreakdown,
            typeBreakdown24h
        );
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestGlucoseTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var sgTimestamp = await _sensorGlucose.GetLatestTimestampAsync(dataSource, cancellationToken);
        var mgTimestamp = await _meterGlucose.GetLatestTimestampAsync(dataSource, cancellationToken);
        var calTimestamp = await _calibrations.GetLatestTimestampAsync(dataSource, cancellationToken);

        return new[] { sgTimestamp, mgTimestamp, calTimestamp }
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Max() is var max && max == default ? null : max;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetOldestGlucoseTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var sgTimestamp = await _sensorGlucose.GetOldestTimestampAsync(dataSource, cancellationToken);
        var mgTimestamp = await _meterGlucose.GetOldestTimestampAsync(dataSource, cancellationToken);
        var calTimestamp = await _calibrations.GetOldestTimestampAsync(dataSource, cancellationToken);

        return new[] { sgTimestamp, mgTimestamp, calTimestamp }
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Min() is var min && min == default ? null : min;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTreatmentTimestampBySourceAsync(
        string dataSource, CancellationToken cancellationToken = default)
    {
        var timestamps = new[]
        {
            await _context.Boluses.AsNoTracking().FromSource(dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.CarbIntakes.AsNoTracking().FromSource(dataSource).OrderByDescending(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BGChecks.AsNoTracking().FromSource(dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.Notes.AsNoTracking().FromSource(dataSource).OrderByDescending(n => n.Timestamp).Select(n => (DateTime?)n.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.DeviceEvents.AsNoTracking().FromSource(dataSource).OrderByDescending(d => d.Timestamp).Select(d => (DateTime?)d.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.TempBasals.AsNoTracking().FromSource(dataSource).OrderByDescending(t => t.StartTimestamp).Select(t => (DateTime?)t.StartTimestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BolusCalculations.AsNoTracking().FromSource(dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
        };
        return timestamps.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max() is var max && max == default ? null : max;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetOldestTreatmentTimestampBySourceAsync(
        string dataSource, CancellationToken cancellationToken = default)
    {
        var timestamps = new[]
        {
            await _context.Boluses.AsNoTracking().FromSource(dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.CarbIntakes.AsNoTracking().FromSource(dataSource).OrderBy(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BGChecks.AsNoTracking().FromSource(dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.Notes.AsNoTracking().FromSource(dataSource).OrderBy(n => n.Timestamp).Select(n => (DateTime?)n.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.DeviceEvents.AsNoTracking().FromSource(dataSource).OrderBy(d => d.Timestamp).Select(d => (DateTime?)d.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.TempBasals.AsNoTracking().FromSource(dataSource).OrderBy(t => t.StartTimestamp).Select(t => (DateTime?)t.StartTimestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BolusCalculations.AsNoTracking().FromSource(dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
        };
        return timestamps.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Min() is var min && min == default ? null : min;
    }
}
