using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// UI Settings controller providing frontend configuration data.
/// This endpoint provides aggregated settings for all frontend settings pages.
/// Supports both GET (read) and PUT (write) operations for settings persistence.
/// </summary>
[ApiController]
[Route("api/v4/ui-settings")]
public class UISettingsController : ControllerBase
{
    private readonly ILogger<UISettingsController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUISettingsService _settingsService;

    public UISettingsController(
        ILogger<UISettingsController> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IUISettingsService settingsService
    )
    {
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Get all UI settings configuration for frontend settings pages.
    /// In demo mode, this fetches from the demo service.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete UI settings configuration</returns>
    [HttpGet]
    [ProducesResponseType(typeof(UISettingsConfiguration), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<UISettingsConfiguration>> GetUISettings(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "UI settings endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Check if demo mode is enabled
            var demoEnabled = _configuration.GetValue<bool>("DemoMode:Enabled");

            if (demoEnabled)
            {
                // Try to fetch from demo service
                var demoServiceUrl = _configuration.GetValue<string>("DemoMode:ServiceUrl");

                if (!string.IsNullOrEmpty(demoServiceUrl))
                {
                    try
                    {
                        var httpClient = _httpClientFactory.CreateClient();
                        var response = await httpClient.GetFromJsonAsync<UISettingsConfiguration>(
                            $"{demoServiceUrl}/ui-settings",
                            cancellationToken
                        );

                        if (response != null)
                        {
                            _logger.LogDebug("Successfully fetched UI settings from demo service");
                            return Ok(response);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to fetch UI settings from demo service, falling back to defaults"
                        );
                    }
                }

                // Fallback: Generate default demo settings locally
                var settings = GenerateDefaultDemoSettings();
                return Ok(settings);
            }

            // In non-demo mode, generate settings from actual configuration/database
            // For now, return default settings structure
            var defaultSettings = GenerateDefaultSettings();
            return Ok(defaultSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving UI settings");
            return StatusCode(500, new { error = "Failed to retrieve UI settings" });
        }
    }

    /// <summary>
    /// Get settings for a specific section.
    /// </summary>
    /// <param name="section">Section name: devices, therapy, algorithm, features, notifications, or services</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Settings for the specified section</returns>
    [HttpGet("{section}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<object>> GetSectionSettings(
        string section,
        CancellationToken cancellationToken = default
    )
    {
        var settings = await GetUISettings(cancellationToken);

        if (
            settings.Result is OkObjectResult okResult
            && okResult.Value is UISettingsConfiguration config
        )
        {
            return section.ToLowerInvariant() switch
            {
                "devices" => Ok(config.Devices),
                "algorithm" => Ok(config.Algorithm),
                "features" => Ok(config.Features),
                "notifications" => Ok(config.Notifications),
                "services" => Ok(config.Services),
                _ => NotFound(new { error = $"Unknown settings section: {section}" }),
            };
        }

        return settings.Result ?? StatusCode(500);
    }

    /// <summary>
    /// Save complete UI settings configuration.
    /// </summary>
    /// <param name="settings">The complete settings to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved settings</returns>
    [HttpPut]
    [ProducesResponseType(typeof(UISettingsConfiguration), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<UISettingsConfiguration>> SaveUISettings(
        [FromBody] UISettingsConfiguration settings,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Saving UI settings from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Check if demo mode is enabled - in demo mode, we don't persist
            var demoEnabled = _configuration.GetValue<bool>("DemoMode:Enabled");
            if (demoEnabled)
            {
                _logger.LogWarning(
                    "Attempted to save settings in demo mode - returning input unchanged"
                );
                return Ok(settings);
            }

            var savedSettings = await _settingsService.SaveSettingsAsync(
                settings,
                cancellationToken
            );
            return Ok(savedSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving UI settings");
            return StatusCode(500, new { error = "Failed to save UI settings" });
        }
    }

    /// <summary>
    /// Save notification settings including alarm configuration.
    /// </summary>
    /// <param name="settings">The notification settings to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved notification settings</returns>
    [HttpPut("notifications")]
    [ProducesResponseType(typeof(NotificationSettings), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<NotificationSettings>> SaveNotificationSettings(
        [FromBody] NotificationSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Saving notification settings");

        try
        {
            var demoEnabled = _configuration.GetValue<bool>("DemoMode:Enabled");
            if (demoEnabled)
            {
                return Ok(settings);
            }

            var savedSettings = await _settingsService.SaveNotificationSettingsAsync(
                settings,
                cancellationToken
            );
            return Ok(savedSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving notification settings");
            return StatusCode(500, new { error = "Failed to save notification settings" });
        }
    }

    /// <summary>
    /// Get alarm profiles configuration (xDrip+-style).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The alarm configuration</returns>
    [HttpGet("notifications/alarms")]
    [ProducesResponseType(typeof(UserAlarmConfiguration), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<UserAlarmConfiguration>> GetAlarmConfiguration(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting alarm configuration");

        try
        {
            var demoEnabled = _configuration.GetValue<bool>("DemoMode:Enabled");
            if (demoEnabled)
            {
                return Ok(GenerateDefaultAlarmConfiguration());
            }

            var config = await _settingsService.GetAlarmConfigurationAsync(cancellationToken);
            return Ok(config ?? GenerateDefaultAlarmConfiguration());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving alarm configuration");
            return StatusCode(500, new { error = "Failed to retrieve alarm configuration" });
        }
    }

    /// <summary>
    /// Save alarm profiles configuration (xDrip+-style).
    /// </summary>
    /// <param name="config">The alarm configuration to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved alarm configuration</returns>
    [HttpPut("notifications/alarms")]
    [ProducesResponseType(typeof(UserAlarmConfiguration), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<UserAlarmConfiguration>> SaveAlarmConfiguration(
        [FromBody] UserAlarmConfiguration config,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Saving alarm configuration with {ProfileCount} profiles",
            config.Profiles?.Count ?? 0
        );

        try
        {
            var demoEnabled = _configuration.GetValue<bool>("DemoMode:Enabled");
            if (demoEnabled)
            {
                return Ok(config);
            }

            var savedConfig = await _settingsService.SaveAlarmConfigurationAsync(
                config,
                cancellationToken
            );
            return Ok(savedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving alarm configuration");
            return StatusCode(500, new { error = "Failed to save alarm configuration" });
        }
    }

    /// <summary>
    /// Save a specific alarm profile.
    /// </summary>
    /// <param name="profile">The alarm profile to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved alarm configuration</returns>
    [HttpPost("notifications/alarms/profiles")]
    [ProducesResponseType(typeof(UserAlarmConfiguration), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<UserAlarmConfiguration>> AddOrUpdateAlarmProfile(
        [FromBody] AlarmProfileConfiguration profile,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Adding/updating alarm profile: {ProfileName}", profile.Name);

        try
        {
            var demoEnabled = _configuration.GetValue<bool>("DemoMode:Enabled");
            if (demoEnabled)
            {
                return Ok(
                    new UserAlarmConfiguration
                    {
                        Profiles = new List<AlarmProfileConfiguration> { profile },
                    }
                );
            }

            var config =
                await _settingsService.GetAlarmConfigurationAsync(cancellationToken)
                ?? new UserAlarmConfiguration();

            // Find existing profile by ID or add new
            var existingIndex = config.Profiles?.FindIndex(p => p.Id == profile.Id) ?? -1;
            if (existingIndex >= 0 && config.Profiles != null)
            {
                config.Profiles[existingIndex] = profile;
            }
            else
            {
                config.Profiles ??= new List<AlarmProfileConfiguration>();
                config.Profiles.Add(profile);
            }

            var savedConfig = await _settingsService.SaveAlarmConfigurationAsync(
                config,
                cancellationToken
            );
            return Ok(savedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving alarm profile");
            return StatusCode(500, new { error = "Failed to save alarm profile" });
        }
    }

    /// <summary>
    /// Delete an alarm profile by ID.
    /// </summary>
    /// <param name="profileId">The profile ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated alarm configuration</returns>
    [HttpDelete("notifications/alarms/profiles/{profileId}")]
    [ProducesResponseType(typeof(UserAlarmConfiguration), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<UserAlarmConfiguration>> DeleteAlarmProfile(
        string profileId,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Deleting alarm profile: {ProfileId}", profileId);

        try
        {
            var demoEnabled = _configuration.GetValue<bool>("DemoMode:Enabled");
            if (demoEnabled)
            {
                return Ok(
                    new UserAlarmConfiguration { Profiles = new List<AlarmProfileConfiguration>() }
                );
            }

            var config = await _settingsService.GetAlarmConfigurationAsync(cancellationToken);
            if (config?.Profiles == null)
            {
                return NotFound(new { error = $"Profile not found: {profileId}" });
            }

            var removed = config.Profiles.RemoveAll(p => p.Id == profileId);
            if (removed == 0)
            {
                return NotFound(new { error = $"Profile not found: {profileId}" });
            }

            var savedConfig = await _settingsService.SaveAlarmConfigurationAsync(
                config,
                cancellationToken
            );
            return Ok(savedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting alarm profile");
            return StatusCode(500, new { error = "Failed to delete alarm profile" });
        }
    }

    private static UserAlarmConfiguration GenerateDefaultAlarmConfiguration()
    {
        return new UserAlarmConfiguration
        {
            Version = 1,
            Enabled = true,
            SoundEnabled = true,
            VibrationEnabled = true,
            GlobalVolume = 80,
            Profiles = new List<AlarmProfileConfiguration>
            {
                new()
                {
                    Id = "default-urgent-low",
                    Name = "Urgent Low",
                    Description = "Critical low glucose alarm",
                    Enabled = true,
                    AlarmType = AlarmTriggerType.UrgentLow,
                    Threshold = 55,
                    PersistenceMinutes = 0,
                    Priority = AlarmPriority.Critical,
                    OverrideQuietHours = true,
                    DisplayOrder = 0,
                    Audio = new AlarmAudioSettings
                    {
                        Enabled = true,
                        SoundId = "alarm-urgent",
                        AscendingVolume = true,
                        StartVolume = 50,
                        MaxVolume = 100,
                        AscendDurationSeconds = 30,
                    },
                    Visual = new AlarmVisualSettings
                    {
                        ScreenFlash = true,
                        FlashColor = "#ff0000",
                        PersistentBanner = true,
                        WakeScreen = true,
                    },
                },
                new()
                {
                    Id = "default-low",
                    Name = "Low",
                    Description = "Low glucose warning",
                    Enabled = true,
                    AlarmType = AlarmTriggerType.Low,
                    Threshold = 70,
                    PersistenceMinutes = 5,
                    Priority = AlarmPriority.High,
                    DisplayOrder = 1,
                    Audio = new AlarmAudioSettings
                    {
                        Enabled = true,
                        SoundId = "alarm-low",
                        AscendingVolume = false,
                        MaxVolume = 80,
                    },
                },
                new()
                {
                    Id = "default-high",
                    Name = "High",
                    Description = "High glucose warning",
                    Enabled = true,
                    AlarmType = AlarmTriggerType.High,
                    Threshold = 180,
                    PersistenceMinutes = 15,
                    Priority = AlarmPriority.Normal,
                    DisplayOrder = 2,
                    Audio = new AlarmAudioSettings
                    {
                        Enabled = true,
                        SoundId = "alarm-high",
                        AscendingVolume = false,
                        MaxVolume = 70,
                    },
                },
                new()
                {
                    Id = "default-urgent-high",
                    Name = "Urgent High",
                    Description = "Critical high glucose alarm",
                    Enabled = true,
                    AlarmType = AlarmTriggerType.UrgentHigh,
                    Threshold = 250,
                    PersistenceMinutes = 10,
                    Priority = AlarmPriority.High,
                    DisplayOrder = 3,
                    Audio = new AlarmAudioSettings
                    {
                        Enabled = true,
                        SoundId = "alarm-urgent",
                        AscendingVolume = true,
                        StartVolume = 40,
                        MaxVolume = 100,
                        AscendDurationSeconds = 45,
                    },
                },
            },
            QuietHours = new QuietHoursConfiguration
            {
                Enabled = false,
                StartTime = "22:00",
                EndTime = "07:00",
                AllowCritical = true,
                ReduceVolume = true,
                QuietVolume = 30,
            },
        };
    }

    private UISettingsConfiguration GenerateDefaultDemoSettings()
    {
        return new UISettingsConfiguration
        {
            Devices = new DeviceSettings
            {
                ConnectedDevices = new List<ConnectedDevice>
                {
                    new()
                    {
                        Id = "demo-cgm-1",
                        Name = "Dexcom G7",
                        Type = "cgm",
                        Status = "connected",
                        Battery = 85,
                        LastSync = DateTimeOffset.UtcNow.AddMinutes(-5),
                        SerialNumber = "SM12345678",
                    },
                    new()
                    {
                        Id = "demo-pump-1",
                        Name = "Omnipod 5",
                        Type = "pump",
                        Status = "connected",
                        Battery = 72,
                        LastSync = DateTimeOffset.UtcNow.AddMinutes(-2),
                        SerialNumber = "POD98765432",
                    },
                },
                AutoConnect = true,
                ShowRawData = false,
                UploadEnabled = true,
                CgmConfiguration = new CgmConfiguration
                {
                    DataSourcePriority = "cgm",
                    SensorWarmupHours = 2,
                },
            },
            Algorithm = new AlgorithmSettings
            {
                Prediction = new PredictionSettings
                {
                    Enabled = true,
                    Minutes = 30,
                    Model = "ar2",
                },
                Autosens = new AutosensSettings
                {
                    Enabled = true,
                    Min = 0.7,
                    Max = 1.2,
                },
                CarbAbsorption = new CarbAbsorptionSettings
                {
                    DefaultMinutes = 30,
                    MinRateGramsPerHour = 4,
                },
                Loop = new LoopSettings
                {
                    Enabled = false,
                    Mode = "open",
                    MaxBasalRate = 4.0,
                    MaxBolus = 10.0,
                    SmbEnabled = false,
                    UamEnabled = false,
                },
                SafetyLimits = new SafetyLimits { MaxIOB = 10.0, MaxDailyBasalMultiplier = 3.0 },
            },
            Features = GenerateDefaultFeatureSettings(),
            Notifications = GenerateDefaultNotificationSettings(),
            Services = GenerateDefaultServicesSettings(),
        };
    }

    private UISettingsConfiguration GenerateDefaultSettings()
    {
        // For non-demo mode, return empty/default structure
        // In a real implementation, this would pull from the database
        return new UISettingsConfiguration
        {
            Devices = new DeviceSettings(),
            Algorithm = new AlgorithmSettings(),
            Features = GenerateDefaultFeatureSettings(),
            Notifications = GenerateDefaultNotificationSettings(),
            Services = new ServicesSettings { AvailableServices = GenerateAvailableServices() },
        };
    }

    private FeatureSettings GenerateDefaultFeatureSettings()
    {
        return new FeatureSettings
        {
            Display = new DisplaySettings
            {
                NightMode = false,
                Theme = "system",
                TimeFormat = "12",
                Units = "mg/dl",
                ShowRawBG = false,
                FocusHours = 3,
            },
            DashboardWidgets = new DashboardWidgets
            {
                GlucoseChart = true,
                Statistics = true,
                Treatments = true,
                Predictions = true,
                Agp = false,
                DailyStats = true,
            },
            Plugins = new Dictionary<string, PluginSettings>
            {
                {
                    "delta",
                    new PluginSettings { Enabled = true, Description = "Show glucose change" }
                },
                {
                    "direction",
                    new PluginSettings { Enabled = true, Description = "Trend arrow indicator" }
                },
                {
                    "timeago",
                    new PluginSettings { Enabled = true, Description = "Time since last reading" }
                },
                {
                    "iob",
                    new PluginSettings { Enabled = true, Description = "Insulin on board" }
                },
                {
                    "cob",
                    new PluginSettings { Enabled = true, Description = "Carbs on board" }
                },
                {
                    "basal",
                    new PluginSettings { Enabled = true, Description = "Current basal rate" }
                },
                {
                    "cage",
                    new PluginSettings { Enabled = false, Description = "Cannula/site age" }
                },
                {
                    "sage",
                    new PluginSettings { Enabled = true, Description = "Sensor age" }
                },
                {
                    "iage",
                    new PluginSettings { Enabled = false, Description = "Insulin reservoir age" }
                },
                {
                    "bage",
                    new PluginSettings { Enabled = false, Description = "Pump battery age" }
                },
                {
                    "pump",
                    new PluginSettings { Enabled = true, Description = "Pump status" }
                },
                {
                    "loop",
                    new PluginSettings { Enabled = true, Description = "Loop/OpenAPS status" }
                },
                {
                    "upbat",
                    new PluginSettings { Enabled = false, Description = "Uploader battery" }
                },
                {
                    "devicestatus",
                    new PluginSettings { Enabled = false, Description = "Device status details" }
                },
                {
                    "bwp",
                    new PluginSettings { Enabled = false, Description = "Bolus wizard preview" }
                },
                {
                    "treatmentnotify",
                    new PluginSettings { Enabled = true, Description = "Treatment notifications" }
                },
                {
                    "openaps",
                    new PluginSettings { Enabled = false, Description = "OpenAPS pill display" }
                },
            },
        };
    }

    private NotificationSettings GenerateDefaultNotificationSettings()
    {
        return new NotificationSettings
        {
            AlarmsEnabled = true,
            SoundEnabled = true,
            VibrationEnabled = true,
            Volume = 70,
            Alarms = new AlarmSettings(),
            QuietHours = new QuietHoursSettings(),
            Channels = new NotificationChannels(),
            EmergencyContacts = new List<EmergencyContact>(),
        };
    }

    private ServicesSettings GenerateDefaultServicesSettings()
    {
        return new ServicesSettings
        {
            ConnectedServices = new List<ConnectedService>
            {
                new()
                {
                    Id = "dexcom-share-1",
                    Name = "Dexcom Share",
                    Type = "cgm",
                    Description = "Dexcom G7 - Share account",
                    Status = "connected",
                    LastSync = DateTimeOffset.UtcNow.AddMinutes(-2),
                    Icon = "dexcom",
                    Configured = true,
                    Enabled = true,
                },
                new()
                {
                    Id = "nightscout-backup-1",
                    Name = "Nightscout Backup",
                    Type = "data",
                    Description = "yoursite.herokuapp.com",
                    Status = "connected",
                    LastSync = DateTimeOffset.UtcNow.AddMinutes(-15),
                    Icon = "nightscout",
                    Configured = true,
                    Enabled = true,
                },
            },
            AvailableServices = GenerateAvailableServices(),
            SyncSettings = new SyncSettings
            {
                AutoSync = true,
                SyncOnAppOpen = true,
                BackgroundRefresh = true,
            },
        };
    }

    private List<AvailableService> GenerateAvailableServices()
    {
        return new List<AvailableService>
        {
            new()
            {
                Id = "dexcom",
                Name = "Dexcom",
                Type = "cgm",
                Description = "Connect to Dexcom Share or Clarity",
                Icon = "dexcom",
            },
            new()
            {
                Id = "freestyle",
                Name = "FreeStyle Libre",
                Type = "cgm",
                Description = "Connect to LibreView for CGM data",
                Icon = "libre",
            },
            new()
            {
                Id = "medtronic",
                Name = "Medtronic CareLink",
                Type = "pump",
                Description = "Sync data from MiniMed pumps",
                Icon = "medtronic",
            },
            new()
            {
                Id = "nightscout",
                Name = "Nightscout",
                Type = "data",
                Description = "Sync with an existing Nightscout instance",
                Icon = "nightscout",
            },
            new()
            {
                Id = "glooko",
                Name = "Glooko",
                Type = "data",
                Description = "Import data from Glooko platform",
                Icon = "glooko",
            },
            new()
            {
                Id = "myfitnesspal",
                Name = "MyFitnessPal",
                Type = "food",
                Description = "Import meals and nutrition data",
                Icon = "myfitnesspal",
            },
        };
    }
}
