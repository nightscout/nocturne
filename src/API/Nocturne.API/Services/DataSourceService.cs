using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

/// <summary>
/// Service for managing and querying data sources connected to Nocturne
/// </summary>
public class DataSourceService : IDataSourceService
{
    private readonly NocturneDbContext _context;
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly ILogger<DataSourceService> _logger;

    public DataSourceService(
        NocturneDbContext context,
        IPostgreSqlService postgreSqlService,
        ILogger<DataSourceService> logger
    )
    {
        _context = context;
        _postgreSqlService = postgreSqlService;
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
        var last24HoursMills = last24Hours.ToUnixTimeMilliseconds();

        // Get distinct devices from entries in the last 30 days
        var thirtyDaysAgo = now.AddDays(-30).ToUnixTimeMilliseconds();

        var entryDevices = await _context
            .Entries.Where(e => e.Mills >= thirtyDaysAgo && e.Device != null && e.Device != "")
            .GroupBy(e => e.Device)
            .Select(g => new
            {
                Device = g.Key!,
                DataSource = g.Max(e => e.DataSource),
                LastMills = g.Max(e => e.Mills),
                FirstMills = g.Min(e => e.Mills),
                TotalCount = g.LongCount(),
                Last24HCount = g.Count(e => e.Mills >= last24HoursMills),
            })
            .ToListAsync(cancellationToken);

        // Also check device status for devices that might not have entries
        var deviceStatusDevices = await _context
            .DeviceStatuses.Where(ds =>
                ds.Mills >= thirtyDaysAgo && ds.Device != null && ds.Device != ""
            )
            .GroupBy(ds => ds.Device)
            .Select(g => new { Device = g.Key!, LastMills = g.Max(ds => ds.Mills) })
            .ToListAsync(cancellationToken);

        var dataSources = new List<DataSourceInfo>();

        foreach (var device in entryDevices)
        {
            var info = CreateDataSourceInfo(device.Device, device.DataSource);
            info.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(device.LastMills);
            info.FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(device.FirstMills);
            info.TotalEntries = device.TotalCount;
            info.EntriesLast24Hours = device.Last24HCount;

            // Check if there's device status data
            var dsDevice = deviceStatusDevices.FirstOrDefault(d => d.Device == device.Device);
            if (dsDevice != null && dsDevice.LastMills > device.LastMills)
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
    public List<AvailableConnector> GetAvailableConnectors()
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


        foreach (var connector in connectors)
        {
            connector.IsConfigured = ConnectorMetadataService.GetByConnectorId(connector.Id) != null;
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
                Name = "xDrip+",
                Platform = "android",
                Category = "cgm",
                Description =
                    "Popular Android CGM app supporting many sensors. Can upload data directly to Nocturne.",
                Icon = "xdrip",
                Url = "https://github.com/NightscoutFoundation/xDrip",
                SetupInstructions = new List<SetupStep>
                {
                    new()
                    {
                        Step = 1,
                        Title = "Open xDrip+ Settings",
                        Description = "Tap the hamburger menu (☰) and select Settings.",
                    },
                    new()
                    {
                        Step = 2,
                        Title = "Cloud Upload",
                        Description = "Navigate to Cloud Upload → Nightscout Sync (REST-API).",
                    },
                    new()
                    {
                        Step = 3,
                        Title = "Enable Upload",
                        Description = "Enable 'Nightscout Sync (REST-API)'.",
                    },
                    new()
                    {
                        Step = 4,
                        Title = "Set Base URL",
                        Description =
                            "Enter your Nocturne URL with API secret: https://YOUR-API-SECRET@your-nocturne-url.com/api/v1",
                    },
                    new()
                    {
                        Step = 5,
                        Title = "Test Connection",
                        Description = "Tap 'Test Connection' to verify the setup.",
                    },
                },
            },
            new()
            {
                Id = "spike",
                Name = "Spike",
                Platform = "ios",
                Category = "cgm",
                Description = "iOS CGM app supporting Dexcom, Libre, and other sensors.",
                Icon = "spike",
                Url = "https://spike-app.com",
                SetupInstructions = new List<SetupStep>
                {
                    new()
                    {
                        Step = 1,
                        Title = "Open Spike Settings",
                        Description = "Go to Settings in the Spike app.",
                    },
                    new()
                    {
                        Step = 2,
                        Title = "Integration",
                        Description = "Navigate to Integration → Nightscout.",
                    },
                    new()
                    {
                        Step = 3,
                        Title = "Configure URL",
                        Description = "Enter your Nocturne URL.",
                    },
                    new()
                    {
                        Step = 4,
                        Title = "API Secret",
                        Description = "Enter your API secret.",
                    },
                    new()
                    {
                        Step = 5,
                        Title = "Enable Upload",
                        Description = "Toggle on 'Upload readings to Nightscout'.",
                    },
                },
            },
            new()
            {
                Id = "loop",
                Name = "Loop",
                Platform = "ios",
                Category = "aid-system",
                Description = "DIY automated insulin delivery system for iOS.",
                Icon = "loop",
                Url = "https://loopkit.github.io/loopdocs/",
                SetupInstructions = new List<SetupStep>
                {
                    new()
                    {
                        Step = 1,
                        Title = "Open Loop Settings",
                        Description = "Go to Loop Settings in the app.",
                    },
                    new()
                    {
                        Step = 2,
                        Title = "Services",
                        Description = "Navigate to Services → Nightscout.",
                    },
                    new()
                    {
                        Step = 3,
                        Title = "Configure",
                        Description = "Enter your Nocturne URL and API secret.",
                    },
                    new()
                    {
                        Step = 4,
                        Title = "Enable",
                        Description = "Toggle on Nightscout upload.",
                    },
                },
            },
            new()
            {
                Id = "aaps",
                Name = "AAPS (AndroidAPS)",
                Platform = "android",
                Category = "aid-system",
                Description = "DIY automated insulin delivery system for Android.",
                Icon = "aaps",
                Url = "https://wiki.aaps.app",
                SetupInstructions = new List<SetupStep>
                {
                    new()
                    {
                        Step = 1,
                        Title = "Open AAPS Config Builder",
                        Description = "Navigate to Config Builder in AAPS.",
                    },
                    new()
                    {
                        Step = 2,
                        Title = "Enable NSClient",
                        Description = "Enable NSClient or NSClientV3 under Synchronization.",
                    },
                    new()
                    {
                        Step = 3,
                        Title = "Configure URL",
                        Description = "Enter your Nocturne URL in NSClient settings.",
                    },
                    new()
                    {
                        Step = 4,
                        Title = "API Secret",
                        Description = "Enter your API secret (use SHA-1 hash if using v1).",
                    },
                    new()
                    {
                        Step = 5,
                        Title = "Select Data",
                        Description =
                            "Choose which data to upload (BG, treatments, profiles, etc.).",
                    },
                },
            },
            new()
            {
                Id = "trio",
                Name = "Trio",
                Platform = "ios",
                Category = "aid-system",
                Description = "Open source automated insulin delivery system for iOS.",
                Icon = "trio",
                Url = "https://diy-trio.org",
                SetupInstructions = new List<SetupStep>
                {
                    new()
                    {
                        Step = 1,
                        Title = "Open Trio Settings",
                        Description = "Go to Settings in the Trio app.",
                    },
                    new()
                    {
                        Step = 2,
                        Title = "Services",
                        Description = "Navigate to Services → Nightscout.",
                    },
                    new()
                    {
                        Step = 3,
                        Title = "Configure",
                        Description = "Enter your Nocturne URL and API secret.",
                    },
                    new()
                    {
                        Step = 4,
                        Title = "Enable Upload",
                        Description = "Enable the data you want to upload.",
                    },
                },
            },
            new()
            {
                Id = "iaps",
                Name = "iAPS",
                Platform = "ios",
                Category = "aid-system",
                Description =
                    "Open source automated insulin delivery system for iOS (fork of OpenAPS).",
                Icon = "iaps",
                Url = "https://iaps.readthedocs.io",
                SetupInstructions = new List<SetupStep>
                {
                    new()
                    {
                        Step = 1,
                        Title = "Open iAPS Settings",
                        Description = "Go to Settings in the iAPS app.",
                    },
                    new()
                    {
                        Step = 2,
                        Title = "Nightscout",
                        Description = "Navigate to Services → Nightscout.",
                    },
                    new()
                    {
                        Step = 3,
                        Title = "Configure URL",
                        Description = "Enter your Nocturne URL.",
                    },
                    new()
                    {
                        Step = 4,
                        Title = "API Secret",
                        Description = "Enter your API secret.",
                    },
                },
            },
            new()
            {
                Id = "nightscout-uploader",
                Name = "Nightscout Uploader",
                Platform = "android",
                Category = "uploader",
                Description = "Android app for uploading data from various BG meters and CGMs.",
                Icon = "nightscout",
                Url = "https://github.com/nightscout/android-uploader",
                SetupInstructions = new List<SetupStep>
                {
                    new()
                    {
                        Step = 1,
                        Title = "Install App",
                        Description = "Install from Google Play or GitHub releases.",
                    },
                    new()
                    {
                        Step = 2,
                        Title = "Configure",
                        Description = "Enter your Nocturne URL.",
                    },
                    new()
                    {
                        Step = 3,
                        Title = "API Secret",
                        Description = "Enter your API secret.",
                    },
                    new()
                    {
                        Step = 4,
                        Title = "Select Source",
                        Description = "Choose your data source device.",
                    },
                },
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
            AvailableConnectors = GetAvailableConnectors(),
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
        if (lowerDevice.Contains("xdrip") || lowerDevice.StartsWith("xdrip"))
        {
            info.Name = "xDrip+";
            info.SourceType = "xdrip";
            info.Category = "cgm";
            info.Icon = "xdrip";
            info.Description = ExtractDeviceDescription(deviceId, "xDrip+ on");
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
            return new ConnectorDataSummary
            {
                ConnectorId = connectorId,
                Entries = 0,
                Treatments = 0,
                DeviceStatuses = 0,
            };
        }

        var deviceId = metadata.DataSourceId;

        var entriesCount = await _context
            .Entries.Where(e => e.DataSource == deviceId)
            .LongCountAsync(cancellationToken);

        var treatmentsCount = await _context
            .Treatments.Where(t => t.DataSource == deviceId)
            .LongCountAsync(cancellationToken);

        var deviceStatusCount = await _context
            .DeviceStatuses.Where(ds => ds.Device == deviceId)
            .LongCountAsync(cancellationToken);

        return new ConnectorDataSummary
        {
            ConnectorId = connectorId,
            Entries = entriesCount,
            Treatments = treatmentsCount,
            DeviceStatuses = deviceStatusCount,
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

            // Delete using the connector's data source ID
            // This avoids the 30-day lookback window limitation in GetActiveDataSourcesAsync
            // Use DataSource field which is what connectors use to identify their data
            var entriesDeleted = await _context
                .Entries.Where(e => e.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            var treatmentsDeleted = await _context
                .Treatments.Where(t => t.DataSource == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            var deviceStatusDeleted = await _context
                .DeviceStatuses.Where(ds => ds.Device == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            _logger.LogInformation(
                "Deleted data for connector {ConnectorId} (device {DeviceId}): {EntriesDeleted} entries, {TreatmentsDeleted} treatments, {DeviceStatusDeleted} device status records",
                connectorId,
                deviceId,
                entriesDeleted,
                treatmentsDeleted,
                deviceStatusDeleted
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = deviceId,
                EntriesDeleted = entriesDeleted,
                TreatmentsDeleted = treatmentsDeleted,
                DeviceStatusDeleted = deviceStatusDeleted,
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

            // Determine the filter to use - prefer device ID for entries
            var deviceId = source.DeviceId;

            // Delete entries by device
            var entriesDeleted = await _context
                .Entries.Where(e => e.Device == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            // Delete treatments by device (enteredBy field)
            var treatmentsDeleted = await _context
                .Treatments.Where(t => t.EnteredBy == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            // Delete device status by device
            var deviceStatusDeleted = await _context
                .DeviceStatuses.Where(ds => ds.Device == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            _logger.LogInformation(
                "Deleted data for {DeviceId}: {EntriesDeleted} entries, {TreatmentsDeleted} treatments, {DeviceStatusDeleted} device status records",
                deviceId,
                entriesDeleted,
                treatmentsDeleted,
                deviceStatusDeleted
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = deviceId,
                EntriesDeleted = entriesDeleted,
                TreatmentsDeleted = treatmentsDeleted,
                DeviceStatusDeleted = deviceStatusDeleted,
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
            // Delete entries by data source
            var entriesDeleted = await _postgreSqlService.DeleteEntriesByDataSourceAsync(
                DataSources.DemoService,
                cancellationToken
            );

            // Delete treatments by data source
            var treatmentsDeleted = await _postgreSqlService.DeleteTreatmentsByDataSourceAsync(
                DataSources.DemoService,
                cancellationToken
            );

            // Delete device status - demo data uses the demo-service device
            var deviceStatusDeleted = await _context
                .DeviceStatuses.Where(ds => ds.Device == DataSources.DemoService)
                .ExecuteDeleteAsync(cancellationToken);

            _logger.LogInformation(
                "Deleted demo data: {EntriesDeleted} entries, {TreatmentsDeleted} treatments, {DeviceStatusDeleted} device status records",
                entriesDeleted,
                treatmentsDeleted,
                deviceStatusDeleted
            );

            return new DataSourceDeleteResult
            {
                Success = true,
                DataSource = DataSources.DemoService,
                EntriesDeleted = entriesDeleted,
                TreatmentsDeleted = treatmentsDeleted,
                DeviceStatusDeleted = deviceStatusDeleted,
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
}
