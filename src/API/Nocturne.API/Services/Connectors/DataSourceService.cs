using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Domain service for querying and managing the data sources (connectors and direct Nightscout uploads)
/// connected to a Nocturne tenant. Aggregates connector metadata, last-seen timestamps from entries
/// and treatments, and enabled/disabled state for display in the admin UI.
/// </summary>
/// <seealso cref="IDataSourceService"/>
public class DataSourceService : IDataSourceService
{
    /// <summary>
    /// How far back discovery counts records. Published on every <see cref="DataSourceInfo"/> as its
    /// <see cref="DataSourceInfo.TotalEntriesWindowDays"/>.
    /// </summary>
    internal const int DiscoveryWindowDays = 30;

    private readonly ISourceOwnedDataReader _sourceData;
    private readonly ISourceOwnedDataPurge _sourcePurge;
    private readonly IDemoDataPurge _demoPurge;
    private readonly IConnectorConfigurationRepository _configurations;
    private readonly ISensorGlucoseRepository _sensorGlucose;
    private readonly IMeterGlucoseRepository _meterGlucose;
    private readonly ICalibrationRepository _calibrations;
    private readonly IAuditContext _auditContext;
    private readonly IConnectorConfigurationService _connectorConfiguration;
    private readonly ProfileActivationWriter _profileActivationWriter;
    private readonly IEnumerable<IConnectorPurgeGuard> _purgeGuards;
    private readonly IEnumerable<IConnectorDataPurge> _dataPurges;
    private readonly TimeProvider _time;
    private readonly ILogger<DataSourceService> _logger;

    public DataSourceService(
        ISourceOwnedDataReader sourceData,
        ISourceOwnedDataPurge sourcePurge,
        IDemoDataPurge demoPurge,
        IConnectorConfigurationRepository configurations,
        ISensorGlucoseRepository sensorGlucose,
        IMeterGlucoseRepository meterGlucose,
        ICalibrationRepository calibrations,
        IAuditContext auditContext,
        IConnectorConfigurationService connectorConfiguration,
        ProfileActivationWriter profileActivationWriter,
        IEnumerable<IConnectorPurgeGuard> purgeGuards,
        IEnumerable<IConnectorDataPurge> dataPurges,
        TimeProvider time,
        ILogger<DataSourceService> logger
    )
    {
        _sourceData = sourceData ?? throw new ArgumentNullException(nameof(sourceData));
        _sourcePurge = sourcePurge ?? throw new ArgumentNullException(nameof(sourcePurge));
        _demoPurge = demoPurge ?? throw new ArgumentNullException(nameof(demoPurge));
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _profileActivationWriter = profileActivationWriter;
        _sensorGlucose = sensorGlucose;
        _meterGlucose = meterGlucose;
        _calibrations = calibrations;
        _auditContext = auditContext;
        _connectorConfiguration = connectorConfiguration;
        _purgeGuards = purgeGuards;
        _dataPurges = dataPurges;
        _time = time;
        _logger = logger;
    }

    /// <remarks>
    /// <paramref name="Handle"/> is which handle the bucket's key names, or <see langword="null"/>
    /// when no contributing table could tell.
    /// </remarks>
    private record TableStats(long Count, int CountLast24H, DateTime Latest, DateTime? Oldest, SourceHandle? Handle);

    /// <summary>The types a source's treatment totals and first and last treatment times span.</summary>
    private static readonly string[] TreatmentTypes =
    [
        SourceDataTypes.Boluses, SourceDataTypes.CarbIntake, SourceDataTypes.BolusCalculations, SourceDataTypes.Notes,
        SourceDataTypes.DeviceEvents, SourceDataTypes.BGChecks, SourceDataTypes.TempBasals,
    ];

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

    private static Dictionary<string, (int active, int stale)> LoadThresholdOverrides(
        IReadOnlyList<ConnectorConfigurationRecord> configs)
    {
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
        DateTime windowStart, DateTime last24HoursDate, CancellationToken ct)
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

        void Merge(string? key, TableStats add)
        {
            if (string.IsNullOrEmpty(key) || add.Count == 0) return;
            if (result.TryGetValue(key, out var existing))
            {
                result[key] = new TableStats(
                    existing.Count + add.Count,
                    existing.CountLast24H + add.CountLast24H,
                    add.Latest > existing.Latest ? add.Latest : existing.Latest,
                    add.Oldest.HasValue && (!existing.Oldest.HasValue || add.Oldest.Value < existing.Oldest.Value)
                        ? add.Oldest : existing.Oldest,
                    CombineHandles(existing.Handle, add.Handle));
            }
            else
            {
                result[key] = add;
            }
        }

        foreach (var bucket in await _sourceData.BucketsAsync(windowStart, last24HoursDate, ct))
            Merge(bucket.Key, new TableStats(
                bucket.Count, bucket.CountLast24Hours, bucket.Latest, bucket.Oldest, bucket.Handle));

        foreach (var purge in _dataPurges)
        {
            if (ConnectorMetadataService.GetByConnectorId(purge.ConnectorName) is not { } owner) continue;

            foreach (var stats in await purge.DescribeAsync(windowStart, last24HoursDate, ct))
                Merge(owner.DataSourceId, new TableStats(
                    stats.Count, stats.CountLast24Hours, stats.Latest, stats.Oldest, SourceHandle.DataSource));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<List<DataSourceInfo>> GetActiveDataSourcesAsync(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting active data sources");

        var now = _time.GetUtcNow();
        var last24Hours = now.AddHours(-24);
        var windowStart = now.AddDays(-DiscoveryWindowDays).UtcDateTime;
        var last24HoursDate = last24Hours.UtcDateTime;

        // Sibling devices sharing a data source both resolve to its single non-glucose bucket, and only
        // the first of them claims it. The reader returns them in device order, so which one that is
        // is the same on every refresh instead of whatever the grouping happened to return.
        var entryDevices = await _sourceData.GlucoseDevicesAsync(windowStart, last24HoursDate, cancellationToken);

        // Also check APS snapshots for devices that might not have entries. Discovery keys on Device,
        // not DataSource: an entry's identity here is the uploader string that CreateDataSourceInfo
        // parses for name, category and icon, so a connector-imported snapshot still lists its rig.
        // The connector keeps its own entry via GetNonGlucoseStatsAsync, which keys on DataSource.
        var deviceStatusDevices = (await _sourceData.ApsSnapshotDevicesAsync(windowStart, cancellationToken))
            .Select(d => new { d.Device, d.DataSource, LastMills = d.Latest.ToMills() })
            .ToList();

        // Batch-aggregate non-glucose tables
        var nonGlucoseStats = await GetNonGlucoseStatsAsync(windowStart, last24HoursDate, cancellationToken);

        // Load user threshold overrides and connector configs for LastSuccessfulSync
        var connectorConfigs = await _configurations.ListAsync(cancellationToken);
        var thresholdOverrides = LoadThresholdOverrides(connectorConfigs);

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

        void ApplyConnectorAndStatus(DataSourceInfo info, string key)
        {
            var connectorMeta = ConnectorMetadataService.GetByDataSourceId(key);
            if (connectorMeta != null)
            {
                info.ConnectorId = connectorMeta.ConnectorId;
                var connConfig = connectorConfigs.FirstOrDefault(c =>
                    c.ConnectorName.Equals(connectorMeta.ConnectorName, StringComparison.OrdinalIgnoreCase));
                if (connConfig?.LastSuccessfulSync != null)
                    info.LastSuccessfulSync = new DateTimeOffset(connConfig.LastSuccessfulSync.Value, TimeSpan.Zero);
            }

            var (activeMinutes, staleMinutes) = ResolveThresholds(key, thresholdOverrides);
            ApplyStatus(info, now, activeMinutes, staleMinutes);
        }

        foreach (var device in entryDevices)
        {
            var info = CreateDataSourceInfo(device.Device, device.DataSource, SourceHandle.Device);
            info.LastSeen = new DateTimeOffset(device.Latest, TimeSpan.Zero);
            info.FirstSeen = new DateTimeOffset(device.Oldest, TimeSpan.Zero);
            info.TotalEntries = device.Count;
            info.EntriesLast24Hours = device.CountLast24Hours;

            // Check if there's device status data
            var dsDevice = deviceStatusDevices.FirstOrDefault(d => d.Device == device.Device);
            var lastSeenMills = device.Latest.ToMills();
            if (dsDevice != null && dsDevice.LastMills > lastSeenMills)
            {
                info.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(dsDevice.LastMills);
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

            ApplyConnectorAndStatus(info, mergeKey);

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

                ApplyConnectorAndStatus(info, dsDevice.DataSource ?? dsDevice.Device);

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

            ApplyConnectorAndStatus(info, key);

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
                Id = connector.ConnectorId,
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
        var configuredSet = new HashSet<string>(
            (await _configurations.ListAsync(cancellationToken)).Select(c => c.ConnectorName),
            StringComparer.OrdinalIgnoreCase);

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
                .Distinct()
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
            TotalEntriesWindowDays = DiscoveryWindowDays,
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

        var counts = new Dictionary<string, long>(
            await _sourceData.CountsBySourceAsync(metadata.DataSourceId, cancellationToken));

        foreach (var purge in PurgesOf(metadata))
        foreach (var stats in await purge.DescribeAsync(null, _time.GetUtcNow().UtcDateTime.AddHours(-24), cancellationToken))
            counts[stats.DataType] = counts.GetValueOrDefault(stats.DataType) + stats.Count;

        return new ConnectorDataSummary
        {
            ConnectorId = connectorId,
            RecordCounts = counts,
        };
    }

    /// <inheritdoc />
    public async Task<DataSourceDeleteResult> DeleteConnectorDataAsync(
        string connectorId,
        bool allowReimport = false,
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

            _logger.LogInformation(
                "Resolved connector {ConnectorId} to device ID {DeviceId}",
                connectorId,
                metadata.DataSourceId
            );

            return await PurgeConnectorDataAsync(metadata, connectorId, allowReimport, cancellationToken);
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
    /// The one purge of a connector's data: holds its syncs off (its purge guard, else the persisted
    /// active flag), deletes everything attributable to its data source, then what it wrote to tables
    /// the source filter cannot reach (<see cref="IConnectorDataPurge"/>).
    /// </summary>
    private async Task<DataSourceDeleteResult> PurgeConnectorDataAsync(
        ConnectorMetadataService.ConnectorDisplayInfo metadata,
        string configurationName,
        bool allowReimport,
        CancellationToken cancellationToken
    )
    {
        var deviceId = metadata.DataSourceId;
        Dictionary<string, long> deletedCounts;
        var guard = _purgeGuards.FirstOrDefault(g =>
            g.ConnectorName.Equals(metadata.ConnectorName, StringComparison.OrdinalIgnoreCase));
        if (guard is not null)
        {
            await using var paused = await guard.PauseSyncsAsync(cancellationToken);
            if (paused is null)
            {
                return DataSourceDeleteResult.Failed(
                    configurationName,
                    DataSourceDeleteError.SyncRunning,
                    $"A sync of connector {configurationName} is running");
            }

            deletedCounts = await DeleteConnectorSourceDataAsync(metadata, allowReimport, cancellationToken);
        }
        else
        {
            var reEnable = allowReimport
                && (await _connectorConfiguration.GetConfigurationAsync(configurationName, cancellationToken))?.IsActive == true;

            // Disable the connector before purging so a scheduled or in-flight sync cannot
            // re-import the data we are about to delete.
            await _connectorConfiguration.SetActiveAsync(
                configurationName, isActive: false, _auditContext.SubjectName, cancellationToken);

            deletedCounts = await DeleteConnectorSourceDataAsync(metadata, allowReimport, cancellationToken);

            // With nothing of the source left stored, the connector's next sync imports its
            // history from the start.
            if (reEnable)
                await _connectorConfiguration.SetActiveAsync(
                    configurationName, isActive: true, _auditContext.SubjectName, cancellationToken);
        }

        _logger.LogInformation(
            "Deleted data for connector {ConnectorId} (device {DeviceId}): {DeletedCounts}",
            configurationName,
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

    private async Task<Dictionary<string, long>> DeleteConnectorSourceDataAsync(
        ConnectorMetadataService.ConnectorDisplayInfo metadata,
        bool allowReimport,
        CancellationToken cancellationToken
    )
    {
        var deletedCounts = await DeleteAllSourceDataAsync(metadata.DataSourceId, allowReimport, cancellationToken);
        foreach (var purge in PurgesOf(metadata))
        {
            foreach (var (type, count) in await purge.PurgeAsync(allowReimport, _auditContext, cancellationToken))
                deletedCounts[type] = deletedCounts.GetValueOrDefault(type) + count;
        }

        return deletedCounts;
    }

    private IEnumerable<IConnectorDataPurge> PurgesOf(ConnectorMetadataService.ConnectorDisplayInfo metadata) =>
        _dataPurges.Where(p => p.ConnectorName.Equals(metadata.ConnectorName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deletes every data type attributable to <paramref name="source"/>: glucose through its V4
    /// repositories, everything else through <see cref="ISourceOwnedDataPurge"/>, whose remarks give
    /// the path each table takes. Returns the per-type deleted counts (non-zero only).
    /// </summary>
    /// <remarks>
    /// With <paramref name="allowReimport"/>, every user tombstone of the source is released after the
    /// delete, including those of earlier deletes, so the source can send its records again.
    /// </remarks>
    private async Task<Dictionary<string, long>> DeleteAllSourceDataAsync(
        string source,
        bool allowReimport,
        CancellationToken cancellationToken
    )
    {
        var sensorGlucoseDeleted = await _sensorGlucose.DeleteBySourceAsync(source, WriteOrigin.Live, cancellationToken);
        var meterGlucoseDeleted = await _meterGlucose.DeleteBySourceAsync(source, WriteOrigin.Live, cancellationToken);
        var calibrationsDeleted = await _calibrations.DeleteBySourceAsync(source, WriteOrigin.Live, cancellationToken);

        var scope = $"data_source={source}";
        var deletedCounts = new Dictionary<string, long>();
        void Add(string type, long deleted)
        {
            if (deleted > 0) deletedCounts[type] = deletedCounts.GetValueOrDefault(type) + deleted;
        }

        Add(nameof(SyncDataType.Glucose), sensorGlucoseDeleted);
        Add(nameof(SyncDataType.ManualBG), meterGlucoseDeleted);
        Add(nameof(SyncDataType.Calibrations), calibrationsDeleted);
        var purged = await _sourcePurge.SoftDeleteBySourceAsync(source, scope, cancellationToken);
        foreach (var (type, deleted) in purged.DeletedCounts)
            Add(type, deleted);
        await _profileActivationWriter.AfterBulkRemovalAsync(purged.ProfileNames, cancellationToken);

        if (allowReimport)
            await _sourcePurge.AllowReimportBySourceAsync(source, scope, cancellationToken);

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
        bool allowReimport = false,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Deleting data for data source: {DataSourceId}", dataSourceId);

        try
        {
            var owner = ConnectorMetadataService.GetByDataSourceId(dataSourceId);
            if (owner is not null)
                return await PurgeConnectorDataAsync(owner, owner.ConnectorName, allowReimport, cancellationToken);

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

            var connector = ConnectorMetadataService.GetByDataSourceId(deviceId);
            if (connector is not null)
                return await PurgeConnectorDataAsync(connector, connector.ConnectorName, allowReimport, cancellationToken);

            var deletedCounts = await DeleteAllSourceDataAsync(deviceId, allowReimport, cancellationToken);

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
            var (glucoseDeleted, treatmentsDeleted, deviceStatusDeleted) = await _demoPurge.PurgeAsync(cancellationToken);

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
        var now = _time.GetUtcNow();
        var oneDayAgoDate = now.AddHours(-24).UtcDateTime;

        var stats = await _sourceData.StatsBySourceAsync(dataSource, oneDayAgoDate, cancellationToken);
        SourceTypeStats Of(string type) => stats.First(s => s.DataType == type);

        var typeBreakdown = new Dictionary<string, long>();
        var typeBreakdown24h = new Dictionary<string, int>();
        foreach (var type in stats.Where(s => s.Count > 0))
        {
            typeBreakdown[type.DataType] = type.Count;
            typeBreakdown24h[type.DataType] = type.CountLast24Hours;
        }

        var sgStats = Of(SourceDataTypes.Glucose);
        var stateSpanStats = Of(SourceDataTypes.StateSpans);
        var treatments = TreatmentTypes.Select(Of).ToList();

        // Treatment totals cover exactly the types the first/last treatment timestamps span, so a
        // source with only one of them cannot report zero treatments alongside a treatment time.
        var totalTreatments = treatments.Sum(t => t.Count);
        var treatments24h = treatments.Sum(t => t.CountLast24Hours);
        var lastTreatmentTime = await GetLatestTreatmentTimestampBySourceAsync(dataSource, cancellationToken);
        var firstTreatmentTime = await GetOldestTreatmentTimestampBySourceAsync(dataSource, cancellationToken);

        return new DataSourceStats(
            dataSource,
            sgStats.Count,
            sgStats.CountLast24Hours,
            sgStats.Latest,
            sgStats.Oldest,
            totalTreatments,
            treatments24h,
            lastTreatmentTime,
            firstTreatmentTime,
            stateSpanStats.Count,
            stateSpanStats.CountLast24Hours,
            stateSpanStats.Latest,
            stateSpanStats.Oldest,
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
    public Task<DateTime?> GetLatestTreatmentTimestampBySourceAsync(
        string dataSource, CancellationToken cancellationToken = default) =>
        _sourceData.LatestTreatmentTimestampAsync(dataSource, cancellationToken);

    /// <inheritdoc />
    public Task<DateTime?> GetOldestTreatmentTimestampBySourceAsync(
        string dataSource, CancellationToken cancellationToken = default) =>
        _sourceData.OldestTreatmentTimestampAsync(dataSource, cancellationToken);
}
