using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// UI Settings controller providing frontend configuration data.
/// This endpoint provides aggregated settings for all frontend settings pages.
/// Supports both GET (read) and PUT (write) operations for settings persistence.
/// </summary>
/// <seealso cref="IUISettingsService"/>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/ui-settings")]
[ClientPropertyName("uiSettings")]
[Authorize]
public class UISettingsController : ControllerBase, IWriteScopedController
{
    /// <summary>
    /// The OAuth scope every write action on this controller requires. These writes are
    /// tenant-wide configuration, not per-user presentation state:
    /// <see cref="IUISettingsService.SaveSettingsAsync"/> takes no subject, and
    /// <see cref="UISettingsConfiguration"/> carries <see cref="NotificationSettings"/> — the alarm
    /// thresholds and profiles that decide whether a low-glucose alert fires — so the whole-blob PUT
    /// reaches alarm behaviour as directly as the <c>/notifications/alarms</c> routes do. V1 and V2
    /// gate their notification writes on <c>alerts.readwrite</c>. The class-level <c>[Authorize]</c>
    /// alone is satisfied by read-only credentials such as a guest-link session, which holds
    /// <c>alerts.read</c>.
    /// </summary>
    public string WriteScope => OAuthScopes.AlertsReadWrite;

    private readonly ILogger<UISettingsController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUISettingsService _settingsService;

    /// <summary>
    /// Initializes a new instance of <see cref="UISettingsController"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configuration">Application configuration (used for DemoMode settings).</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients to proxy demo service calls.</param>
    /// <param name="settingsService">Service for persisting and retrieving UI settings.</param>
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

                return Ok(GenerateDemoSettings());
            }

            // Read what was persisted by SaveUISettings; the service falls back to
            // defaults when the tenant has never saved. Returning freshly generated
            // defaults here instead meant a saved setting never came back on the
            // next load, so every settings page appeared to revert on reload.
            var settings = await _settingsService.GetSettingsAsync(cancellationToken);

            // The connector catalog is static metadata, not persisted tenant state.
            settings.Services ??= new ServicesSettings();
            settings.Services.AvailableServices = GenerateAvailableServices();

            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving UI settings");
            return Problem(detail: "Failed to retrieve UI settings", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get settings for a specific section.
    /// </summary>
    /// <param name="section">Name of any <see cref="UISettingsSections"/> entry</param>
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
            return UISettingsSections.Find(section) is { } known
                ? Ok(known.Get(config))
                : Problem(
                    detail: $"Unknown settings section: {section}",
                    statusCode: 404,
                    title: "Not Found"
                );
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
    [RequireDeclaredWriteScope]
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
            return Problem(detail: "Failed to save UI settings", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Save notification settings including alarm configuration.
    /// </summary>
    /// <param name="settings">The notification settings to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved notification settings</returns>
    [HttpPut("notifications")]
    [RequireDeclaredWriteScope]
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
            return Problem(detail: "Failed to save notification settings", statusCode: 500, title: "Internal Server Error");
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
                return Ok(GenerateDemoAlarmConfiguration());
            }

            var config = await _settingsService.GetAlarmConfigurationAsync(cancellationToken);

            return config == null ? AlarmConfigurationUnavailable() : Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving alarm configuration");
            return Problem(detail: "Failed to retrieve alarm configuration", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Save alarm profiles configuration (xDrip+-style).
    /// </summary>
    /// <param name="config">The alarm configuration to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved alarm configuration</returns>
    [HttpPut("notifications/alarms")]
    [RequireDeclaredWriteScope]
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
            return Problem(detail: "Failed to save alarm configuration", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Save a specific alarm profile.
    /// </summary>
    /// <param name="profile">The alarm profile to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved alarm configuration</returns>
    [HttpPost("notifications/alarms/profiles")]
    [RequireDeclaredWriteScope]
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

            var config = await _settingsService.GetAlarmConfigurationAsync(cancellationToken);
            if (config == null)
            {
                return AlarmConfigurationUnavailable();
            }

            config.Profiles ??= [];

            // A blank id would match every other blank-id profile on the next upsert, so the
            // list could never hold more than one of them.
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.CreateVersion7().ToString();
            }

            var existing = config.Profiles.FindIndex(p => p.Id == profile.Id);
            if (existing >= 0)
            {
                config.Profiles[existing] = profile;
            }
            else
            {
                config.Profiles.Add(profile);
            }

            return Ok(
                await _settingsService.SaveAlarmConfigurationAsync(config, cancellationToken)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving alarm profile");
            return Problem(detail: "Failed to save alarm profile", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Delete an alarm profile by ID.
    /// </summary>
    /// <param name="profileId">The profile ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated alarm configuration</returns>
    [HttpDelete("notifications/alarms/profiles/{profileId}")]
    [RequireDeclaredWriteScope]
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
            if (config == null)
            {
                return AlarmConfigurationUnavailable();
            }

            config.Profiles ??= [];

            if (config.Profiles.RemoveAll(p => p.Id == profileId) == 0)
            {
                return Problem(
                    detail: $"Alarm profile not found: {profileId}",
                    statusCode: 404,
                    title: "Not Found"
                );
            }

            return Ok(
                await _settingsService.SaveAlarmConfigurationAsync(config, cancellationToken)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting alarm profile");
            return Problem(detail: "Failed to delete alarm profile", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// <see cref="IUISettingsService.GetAlarmConfigurationAsync"/> normalizes every successful read
    /// to a configuration, so null means the read failed. Substituting a value here would let the
    /// following write replace the tenant's profiles.
    /// </summary>
    private ObjectResult AlarmConfigurationUnavailable()
    {
        return Problem(
            detail: "Failed to retrieve alarm configuration",
            statusCode: 500,
            title: "Internal Server Error"
        );
    }

    /// <summary>
    /// Sample alarm profiles for the demo tenant. Real tenants are deliberately not seeded with
    /// these: profiles nobody configured would fire alarms nobody asked for.
    /// </summary>
    private static UserAlarmConfiguration GenerateDemoAlarmConfiguration()
    {
        return new UserAlarmConfiguration
        {
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
        };
    }

    /// <summary>
    /// The demo tenant's sample devices and services. Every other section is left at the
    /// <see cref="UISettingsConfiguration"/> defaults.
    /// </summary>
    private UISettingsConfiguration GenerateDemoSettings()
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
            },
            Features = new FeatureSettings { Plugins = GenerateDemoPlugins() },
            Services = new ServicesSettings
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
            },
        };
    }

    /// <summary>
    /// The plugin pills the demo tenant shows. <see cref="FeatureSettings.Plugins"/> has no defaults,
    /// so there is nothing to inherit here.
    /// </summary>
    private static Dictionary<string, PluginSettings> GenerateDemoPlugins()
    {
        return new Dictionary<string, PluginSettings>
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
        };
    }

    private List<AvailableService> GenerateAvailableServices()
    {
        return ConnectorMetadataService.GetAvailableServices();
    }
}
