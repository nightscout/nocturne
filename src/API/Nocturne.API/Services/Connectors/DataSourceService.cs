using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;

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
    private readonly ILogger<DataSourceService> _logger;

    public DataSourceService(
        NocturneDbContext context,
        ISensorGlucoseRepository sensorGlucose,
        IMeterGlucoseRepository meterGlucose,
        ICalibrationRepository calibrations,
        ILogger<DataSourceService> logger
    )
    {
        _context = context;
        _sensorGlucose = sensorGlucose;
        _meterGlucose = meterGlucose;
        _calibrations = calibrations;
        _logger = logger;
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
            .ToListAsync(cancellationToken);

        // Also check APS snapshots for devices that might not have entries
        var thirtyDaysAgo = now.AddDays(-30).UtcDateTime;
        var deviceStatusDevices = await _context
            .ApsSnapshots.Where(ds =>
                ds.Timestamp >= thirtyDaysAgo && ds.Device != null && ds.Device != ""
            )
            .GroupBy(ds => ds.Device)
            .Select(g => new { Device = g.Key!, LastMills = new DateTimeOffset(g.Max(ds => ds.Timestamp), TimeSpan.Zero).ToUnixTimeMilliseconds() })
            .ToListAsync(cancellationToken);

        var dataSources = new List<DataSourceInfo>();

        foreach (var device in entryDevices)
        {
            var info = CreateDataSourceInfo(device.Device, device.DataSource);
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

            // Calculate status
            var minutesSinceLast = (int)(now - info.LastSeen.Value).TotalMinutes;
            info.MinutesSinceLastData = minutesSinceLast;
            info.Status = minutesSinceLast switch
            {
                < 15 => "active",
                < 60 => "stale",
                _ => "inactive",
            };

            dataSources.Add(info);
        }

        // Add any device status only devices
        foreach (var dsDevice in deviceStatusDevices)
        {
            if (!dataSources.Any(d => d.DeviceId == dsDevice.Device))
            {
                var info = CreateDataSourceInfo(dsDevice.Device, null);
                info.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(dsDevice.LastMills);
                info.FirstSeen = info.LastSeen;
                info.TotalEntries = 0;
                info.EntriesLast24Hours = 0;

                var minutesSinceLast = (int)(now - info.LastSeen.Value).TotalMinutes;
                info.MinutesSinceLastData = minutesSinceLast;
                info.Status = minutesSinceLast switch
                {
                    < 15 => "active",
                    < 60 => "stale",
                    _ => "inactive",
                };

                dataSources.Add(info);
            }
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
                Url = "https://diy-trio.org",
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
    private DataSourceInfo CreateDataSourceInfo(string deviceId, string? dataSource)
    {
        var info = new DataSourceInfo { Id = GenerateId(deviceId), DeviceId = deviceId };

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
        else if (lowerDevice.Contains("xdrip") || lowerDevice.StartsWith("xdrip"))
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
            lowerDevice.Contains("medtronic")
            || lowerDevice.Contains("minimed")
            || lowerDevice.Contains("carelink")
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
            .SensorGlucose.Where(sg => sg.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (sensorGlucoseCount > 0) counts[nameof(SyncDataType.Glucose)] = sensorGlucoseCount;

        var meterGlucoseCount = await _context
            .MeterGlucose.Where(mg => mg.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (meterGlucoseCount > 0) counts[nameof(SyncDataType.ManualBG)] = meterGlucoseCount;

        var calibrationsCount = await _context
            .Calibrations.Where(c => c.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (calibrationsCount > 0) counts[nameof(SyncDataType.Calibrations)] = calibrationsCount;

        // Treatments
        var bolusCount = await _context
            .Boluses.Where(b => b.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (bolusCount > 0) counts[nameof(SyncDataType.Boluses)] = bolusCount;

        var carbIntakeCount = await _context
            .CarbIntakes.Where(c => c.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (carbIntakeCount > 0) counts[nameof(SyncDataType.CarbIntake)] = carbIntakeCount;

        var bgChecksCount = await _context
            .BGChecks.Where(b => b.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (bgChecksCount > 0) counts[nameof(SyncDataType.BGChecks)] = bgChecksCount;

        var bolusCalcCount = await _context
            .BolusCalculations.Where(bc => bc.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (bolusCalcCount > 0) counts[nameof(SyncDataType.BolusCalculations)] = bolusCalcCount;

        var notesCount = await _context
            .Notes.Where(n => n.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (notesCount > 0) counts[nameof(SyncDataType.Notes)] = notesCount;

        var deviceEventsCount = await _context
            .DeviceEvents.Where(de => de.DataSource == deviceId)
            .LongCountAsync(cancellationToken);
        if (deviceEventsCount > 0) counts[nameof(SyncDataType.DeviceEvents)] = deviceEventsCount;

        var stateSpansCount = await _context
            .StateSpans.Where(s => s.Source == deviceId)
            .LongCountAsync(cancellationToken);
        if (stateSpansCount > 0) counts[nameof(SyncDataType.StateSpans)] = stateSpansCount;

        // Device status
        var deviceStatusCount = await _context
            .ApsSnapshots.Where(ds => ds.Device == deviceId)
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
                return new DataSourceDeleteResult
                {
                    Success = false,
                    DataSource = connectorId,
                    Error = $"Connector not found: {connectorId}",
                };
            }

            // Connector's DataSourceId is what we use in the database (e.g. "dexcom-connector")
            // This is also what the connector uses as the Device field when writing entries
            var deviceId = metadata.DataSourceId;
            _logger.LogInformation(
                "Resolved connector {ConnectorId} to device ID {DeviceId}",
                connectorId,
                deviceId
            );

            // Delete from V4 glucose tables
            var sensorGlucoseDeleted = await _sensorGlucose.DeleteBySourceAsync(deviceId, cancellationToken);
            var meterGlucoseDeleted = await _meterGlucose.DeleteBySourceAsync(deviceId, cancellationToken);
            var calibrationsDeleted = await _calibrations.DeleteBySourceAsync(deviceId, cancellationToken);

            var bolusesDeleted = await _context
                .Boluses.Where(b => b.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var carbIntakesDeleted = await _context
                .CarbIntakes.Where(c => c.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var bgChecksDeleted = await _context
                .BGChecks.Where(b => b.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var notesDeleted = await _context
                .Notes.Where(n => n.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var deviceEventsDeleted = await _context
                .DeviceEvents.Where(de => de.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var bolusCalcsDeleted = await _context
                .BolusCalculations.Where(bc => bc.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            var deviceStatusDeleted = await _context
                .ApsSnapshots.Where(ds => ds.Device == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            var stateSpansDeleted = await _context
                .StateSpans.Where(s => s.Source == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            // Build per-type deletion counts
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
            if (stateSpansDeleted > 0) deletedCounts[nameof(SyncDataType.StateSpans)] = stateSpansDeleted;

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
            return new DataSourceDeleteResult
            {
                Success = false,
                DataSource = connectorId,
                Error = "Failed to delete connector data",
            };
        }
    }

    private static string GenerateId(string deviceId)
    {
        // Create a stable ID from the device identifier
        var hash = deviceId.GetHashCode();
        return $"ds-{Math.Abs(hash):x8}";
    }

    private static string CleanDeviceName(string deviceId)
    {
        // Clean up device ID to make it more readable
        var name = deviceId.Replace("-", " ").Replace("_", " ").Trim();

        // Capitalize first letter of each word
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
    }

    private static string ExtractDeviceDescription(string deviceId, string prefix)
    {
        // Try to extract useful info from device ID
        // e.g., "xDrip-DexcomG6" -> "xDrip+ on DexcomG6"
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
            // Find the data source to get the actual device ID or data source string
            var sources = await GetActiveDataSourcesAsync(cancellationToken);
            var source = sources.FirstOrDefault(s =>
                s.Id == dataSourceId || s.DeviceId == dataSourceId
            );

            if (source == null)
            {
                _logger.LogWarning("Data source not found: {DataSourceId}", dataSourceId);
                return new DataSourceDeleteResult
                {
                    Success = false,
                    DataSource = dataSourceId,
                    Error = $"Data source not found: {dataSourceId}",
                };
            }

            // The deviceId is the raw Device field value from sensor_glucose (e.g. "dexcom-connector",
            // "xDrip-DexcomG6 Samsung Galaxy S21"). For connectors this also matches DataSource.
            // For uploaders, Device is set by the client app and DataSource may differ.
            // We match on both Device and DataSource to cover both cases.
            var deviceId = source.DeviceId;

            // Delete V4 glucose tables by DataSource
            var sensorGlucoseDeleted = await _sensorGlucose.DeleteBySourceAsync(deviceId, cancellationToken);
            var meterGlucoseDeleted = await _meterGlucose.DeleteBySourceAsync(deviceId, cancellationToken);
            var calibrationsDeleted = await _calibrations.DeleteBySourceAsync(deviceId, cancellationToken);
            var bolusesDeleted = await _context
                .Boluses.Where(b => b.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var carbIntakesDeleted = await _context
                .CarbIntakes.Where(c => c.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var bgChecksDeleted = await _context
                .BGChecks.Where(b => b.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var notesDeleted = await _context
                .Notes.Where(n => n.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var deviceEventsDeleted = await _context
                .DeviceEvents.Where(de => de.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            var bolusCalcsDeleted = await _context
                .BolusCalculations.Where(bc => bc.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            // Delete device status by device
            var deviceStatusDeleted = await _context
                .ApsSnapshots.Where(ds => ds.Device == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            var stateSpansDeleted = await _context
                .StateSpans.Where(s => s.Source == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            var deletedCounts = new Dictionary<string, long>();
            if (sensorGlucoseDeleted > 0) deletedCounts[nameof(SyncDataType.Glucose)] = sensorGlucoseDeleted;
            if (meterGlucoseDeleted > 0) deletedCounts[nameof(SyncDataType.ManualBG)] = meterGlucoseDeleted;
            if (calibrationsDeleted > 0) deletedCounts[nameof(SyncDataType.Calibrations)] = calibrationsDeleted;
            if (bolusesDeleted > 0) deletedCounts[nameof(SyncDataType.Boluses)] = bolusesDeleted;
            if (carbIntakesDeleted > 0) deletedCounts[nameof(SyncDataType.CarbIntake)] = carbIntakesDeleted;
            if (bgChecksDeleted > 0) deletedCounts[nameof(SyncDataType.BGChecks)] = bgChecksDeleted;
            if (notesDeleted > 0) deletedCounts[nameof(SyncDataType.Notes)] = notesDeleted;
            if (deviceEventsDeleted > 0) deletedCounts[nameof(SyncDataType.DeviceEvents)] = deviceEventsDeleted;
            if (bolusCalcsDeleted > 0) deletedCounts[nameof(SyncDataType.BolusCalculations)] = bolusCalcsDeleted;
            if (deviceStatusDeleted > 0) deletedCounts[nameof(SyncDataType.DeviceStatus)] = deviceStatusDeleted;
            if (stateSpansDeleted > 0) deletedCounts[nameof(SyncDataType.StateSpans)] = stateSpansDeleted;

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
            return new DataSourceDeleteResult
            {
                Success = false,
                DataSource = dataSourceId,
                Error = ex.Message,
            };
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
            // Delete glucose data from V4 tables
            var glucoseDeleted = await DeleteGlucoseDataBySourceAsync(
                DataSources.DemoService,
                cancellationToken
            );

            // Delete V4 treatment records by data source
            var treatmentsDeleted = await _context.Boluses.Where(b => b.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
            treatmentsDeleted += await _context.CarbIntakes.Where(c => c.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
            treatmentsDeleted += await _context.BGChecks.Where(b => b.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
            treatmentsDeleted += await _context.Notes.Where(n => n.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
            treatmentsDeleted += await _context.DeviceEvents.Where(de => de.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
            treatmentsDeleted += await _context.BolusCalculations.Where(bc => bc.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
            treatmentsDeleted += await _context.TempBasals.Where(t => t.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
            treatmentsDeleted += await _context.StateSpans.Where(s => s.Source == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);

            // Delete APS snapshots - demo data uses the demo-service device
            var deviceStatusDeleted = await _context
                .ApsSnapshots.Where(ds => ds.Device == DataSources.DemoService)
                .ExecuteDeleteAsync(cancellationToken);

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
            return new DataSourceDeleteResult
            {
                Success = false,
                DataSource = DataSources.DemoService,
                Error = ex.Message,
            };
        }
    }

    /// <inheritdoc />
    public async Task<DataSourceStats> GetDataSourceStatsAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTimeOffset.UtcNow;
        var oneDayAgo = now.AddHours(-24).ToUnixTimeMilliseconds();
        var oneDayAgoDate = now.AddHours(-24).UtcDateTime;

        // Query V4 sensor glucose stats
        var sgStats = await _context
            .SensorGlucose.Where(sg => sg.DataSource == dataSource)
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
            .MeterGlucose.Where(mg => mg.DataSource == dataSource)
            .LongCountAsync(cancellationToken);
        var meterGlucose24h = meterGlucoseTotal > 0
            ? await _context.MeterGlucose
                .Where(mg => mg.DataSource == dataSource && mg.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bolusesTotal = await _context
            .Boluses.Where(b => b.DataSource == dataSource)
            .LongCountAsync(cancellationToken);
        var boluses24h = bolusesTotal > 0
            ? await _context.Boluses
                .Where(b => b.DataSource == dataSource && b.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var carbIntakesTotal = await _context
            .CarbIntakes.Where(c => c.DataSource == dataSource)
            .LongCountAsync(cancellationToken);
        var carbIntakes24h = carbIntakesTotal > 0
            ? await _context.CarbIntakes
                .Where(c => c.DataSource == dataSource && c.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var bolusCalcsTotal = await _context
            .BolusCalculations.Where(bc => bc.DataSource == dataSource)
            .LongCountAsync(cancellationToken);
        var bolusCalcs24h = bolusCalcsTotal > 0
            ? await _context.BolusCalculations
                .Where(bc => bc.DataSource == dataSource && bc.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var notesTotal = await _context
            .Notes.Where(n => n.DataSource == dataSource)
            .LongCountAsync(cancellationToken);
        var notes24h = notesTotal > 0
            ? await _context.Notes
                .Where(n => n.DataSource == dataSource && n.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var deviceEventsTotal = await _context
            .DeviceEvents.Where(de => de.DataSource == dataSource)
            .LongCountAsync(cancellationToken);
        var deviceEvents24h = deviceEventsTotal > 0
            ? await _context.DeviceEvents
                .Where(de => de.DataSource == dataSource && de.Timestamp >= oneDayAgoDate)
                .CountAsync(cancellationToken)
            : 0;

        var deviceStatusTotal = await _context
            .ApsSnapshots.Where(ds => ds.Device == dataSource)
            .LongCountAsync(cancellationToken);
        var deviceStatus24h = deviceStatusTotal > 0
            ? await _context.ApsSnapshots
                .Where(ds => ds.Device == dataSource && ds.Timestamp >= DateTimeOffset.FromUnixTimeMilliseconds(oneDayAgo).UtcDateTime)
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
        if ((stateSpanStats?.TotalStateSpans ?? 0) > 0) { typeBreakdown[nameof(SyncDataType.StateSpans)] = stateSpanStats!.TotalStateSpans; typeBreakdown24h[nameof(SyncDataType.StateSpans)] = stateSpanStats.StateSpansLast24Hours; }
        if (deviceStatusTotal > 0) { typeBreakdown[nameof(SyncDataType.DeviceStatus)] = deviceStatusTotal; typeBreakdown24h[nameof(SyncDataType.DeviceStatus)] = deviceStatus24h; }

        var totalTreatments = bolusesTotal + carbIntakesTotal + bolusCalcsTotal + notesTotal + deviceEventsTotal;
        var treatments24h = boluses24h + carbIntakes24h + bolusCalcs24h + notes24h + deviceEvents24h;
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
            await _context.Boluses.AsNoTracking().Where(b => b.DataSource == dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.CarbIntakes.AsNoTracking().Where(c => c.DataSource == dataSource).OrderByDescending(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BGChecks.AsNoTracking().Where(b => b.DataSource == dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.Notes.AsNoTracking().Where(n => n.DataSource == dataSource).OrderByDescending(n => n.Timestamp).Select(n => (DateTime?)n.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.DeviceEvents.AsNoTracking().Where(d => d.DataSource == dataSource).OrderByDescending(d => d.Timestamp).Select(d => (DateTime?)d.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.TempBasals.AsNoTracking().Where(t => t.DataSource == dataSource).OrderByDescending(t => t.StartTimestamp).Select(t => (DateTime?)t.StartTimestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BolusCalculations.AsNoTracking().Where(b => b.DataSource == dataSource).OrderByDescending(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
        };
        return timestamps.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max() is var max && max == default ? null : max;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetOldestTreatmentTimestampBySourceAsync(
        string dataSource, CancellationToken cancellationToken = default)
    {
        var timestamps = new[]
        {
            await _context.Boluses.AsNoTracking().Where(b => b.DataSource == dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.CarbIntakes.AsNoTracking().Where(c => c.DataSource == dataSource).OrderBy(c => c.Timestamp).Select(c => (DateTime?)c.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BGChecks.AsNoTracking().Where(b => b.DataSource == dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.Notes.AsNoTracking().Where(n => n.DataSource == dataSource).OrderBy(n => n.Timestamp).Select(n => (DateTime?)n.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.DeviceEvents.AsNoTracking().Where(d => d.DataSource == dataSource).OrderBy(d => d.Timestamp).Select(d => (DateTime?)d.Timestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.TempBasals.AsNoTracking().Where(t => t.DataSource == dataSource).OrderBy(t => t.StartTimestamp).Select(t => (DateTime?)t.StartTimestamp).FirstOrDefaultAsync(cancellationToken),
            await _context.BolusCalculations.AsNoTracking().Where(b => b.DataSource == dataSource).OrderBy(b => b.Timestamp).Select(b => (DateTime?)b.Timestamp).FirstOrDefaultAsync(cancellationToken),
        };
        return timestamps.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Min() is var min && min == default ? null : min;
    }

    /// <inheritdoc />
    public async Task<long> DeleteGlucoseDataBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    )
    {
        var sgDeleted = await _sensorGlucose.DeleteBySourceAsync(dataSource, cancellationToken);
        var mgDeleted = await _meterGlucose.DeleteBySourceAsync(dataSource, cancellationToken);
        var calDeleted = await _calibrations.DeleteBySourceAsync(dataSource, cancellationToken);

        return sgDeleted + mgDeleted + calDeleted;
    }
}
