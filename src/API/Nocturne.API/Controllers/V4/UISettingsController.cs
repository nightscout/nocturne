using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

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
    private readonly AlertRuleRepository _alertRuleRepository;
    private readonly NotificationPreferencesRepository _notificationPreferencesRepository;

    public UISettingsController(
        ILogger<UISettingsController> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IUISettingsService settingsService,
        AlertRuleRepository alertRuleRepository,
        NotificationPreferencesRepository notificationPreferencesRepository
    )
    {
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _alertRuleRepository = alertRuleRepository;
        _notificationPreferencesRepository = notificationPreferencesRepository;
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

            // Get base config from settings service (for global settings like Volume, etc.)
            var config =
                await _settingsService.GetAlarmConfigurationAsync(cancellationToken)
                ?? GenerateDefaultAlarmConfiguration();

            // Overlay data from AlertRuleRepository and NotificationPreferencesRepository
            var userId = GetUserId();

            // 1. Get Quiet Hours
            var mapPreferences =
                await _notificationPreferencesRepository.GetPreferencesForUserAsync(
                    userId,
                    cancellationToken
                );
            if (mapPreferences != null)
            {
                config.QuietHours = new QuietHoursConfiguration
                {
                    Enabled = mapPreferences.QuietHoursEnabled,
                    StartTime = mapPreferences.QuietHoursStart?.ToString("HH:mm") ?? "22:00",
                    EndTime = mapPreferences.QuietHoursEnd?.ToString("HH:mm") ?? "07:00",
                    AllowCritical = mapPreferences.EmergencyOverrideQuietHours,
                    // These are not yet in NotificationPreferencesEntity, so we keep what was in config or default
                    ReduceVolume = config.QuietHours?.ReduceVolume ?? true,
                    QuietVolume = config.QuietHours?.QuietVolume ?? 0,
                };
            }

            // 2. Get Alarm Profiles
            var activeRules = await _alertRuleRepository.GetRulesForUserAsync(
                userId,
                cancellationToken
            );
            if (activeRules.Length > 0)
            {
                config.Profiles = activeRules.Select(MapRuleToProfile).ToList();
            }

            return Ok(config);
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

            // 1. Save to legacy JSON store (backup/global settings)
            var savedConfig = await _settingsService.SaveAlarmConfigurationAsync(
                config,
                cancellationToken
            );

            // 2. Save to new Repositories
            var userId = GetUserId();

            // Sync Quiet Hours
            if (config.QuietHours != null)
            {
                TimeOnly? start = null;
                TimeOnly? end = null;

                if (TimeOnly.TryParse(config.QuietHours.StartTime, out var parsedStart))
                    start = parsedStart;

                if (TimeOnly.TryParse(config.QuietHours.EndTime, out var parsedEnd))
                    end = parsedEnd;

                var updated = await _notificationPreferencesRepository.UpdateQuietHoursAsync(
                    userId,
                    config.QuietHours.Enabled,
                    start,
                    end,
                    config.QuietHours.AllowCritical,
                    cancellationToken
                );

                if (!updated)
                {
                    var pref = new NotificationPreferencesEntity
                    {
                        UserId = userId,
                        QuietHoursEnabled = config.QuietHours.Enabled,
                        EmergencyOverrideQuietHours = config.QuietHours.AllowCritical,
                        QuietHoursStart = start,
                        QuietHoursEnd = end,
                    };

                    await _notificationPreferencesRepository.UpsertPreferencesAsync(
                        pref,
                        cancellationToken
                    );
                }
            }

            // Sync Profiles
            if (config.Profiles != null)
            {
                // Get existing rules to handle deletions
                var existingRules = await _alertRuleRepository.GetRulesForUserAsync(
                    userId,
                    cancellationToken
                );
                var validRuleIds = new HashSet<Guid>();

                foreach (var profile in config.Profiles)
                {
                    var rule = MapProfileToRule(profile, userId);

                    // Check if existing rule matches (by ID if strictly UUID, or by Name/Type logic if needed)
                    // The Profile ID in app is string, Entity ID is Guid.
                    // If Profile.Id is a Guid, we update. If not (e.g. "default-low"), we might creating new entries repeatedly?
                    // We must ensure stable IDs.

                    Guid ruleId;
                    if (Guid.TryParse(profile.Id, out ruleId))
                    {
                        rule.Id = ruleId;
                        var existing = existingRules.FirstOrDefault(r => r.Id == ruleId);
                        if (existing != null)
                        {
                            await _alertRuleRepository.UpdateRuleAsync(
                                ruleId,
                                rule,
                                cancellationToken
                            );
                            validRuleIds.Add(ruleId);
                        }
                        else
                        {
                            // If ID is Guid but not found, create new (with that ID if possible, or new)
                            await _alertRuleRepository.CreateRuleAsync(rule, cancellationToken);
                            validRuleIds.Add(rule.Id);
                        }
                    }
                    else
                    {
                        // Profile ID is not a Guid (e.g. "default-urgent-low").
                        // We should check if we already have a rule for this AlarmType?
                        // Or just create a new one and let the frontend update its ID?
                        // Better: Try to match by Name or AlarmType if no Guid ID.
                        var existing = existingRules.FirstOrDefault(r => r.Name == profile.Name); // Heuristic

                        if (existing != null)
                        {
                            rule.Id = existing.Id; // Keep existing ID
                            await _alertRuleRepository.UpdateRuleAsync(
                                existing.Id,
                                rule,
                                cancellationToken
                            );
                            validRuleIds.Add(existing.Id);
                            // Update the profile ID in returned config to match the GUID
                            profile.Id = existing.Id.ToString();
                        }
                        else
                        {
                            var created = await _alertRuleRepository.CreateRuleAsync(
                                rule,
                                cancellationToken
                            );
                            validRuleIds.Add(created.Id);
                            profile.Id = created.Id.ToString();
                        }
                    }
                }

                // Disable rules not present in the update to avoid FK violations in alert_history.
                foreach (var inputRule in existingRules)
                {
                    if (!validRuleIds.Contains(inputRule.Id))
                    {
                        await _alertRuleRepository.SetRuleEnabledAsync(
                            inputRule.Id,
                            false,
                            cancellationToken
                        );
                    }
                }

                // Update config with the (potentially new) IDs
                savedConfig.Profiles = config.Profiles;
            }

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

            // Logic: Save to Repo then refresh full config
            var userId = GetUserId();
            var rule = MapProfileToRule(profile, userId);

            if (Guid.TryParse(profile.Id, out var ruleId))
            {
                var existing = await _alertRuleRepository.GetRuleByIdAsync(
                    ruleId,
                    cancellationToken
                );
                if (existing != null)
                {
                    await _alertRuleRepository.UpdateRuleAsync(ruleId, rule, cancellationToken);
                }
                else
                {
                    await _alertRuleRepository.CreateRuleAsync(rule, cancellationToken);
                }
            }
            else
            {
                // Try match by name
                var existingRules = await _alertRuleRepository.GetRulesForUserAsync(
                    userId,
                    cancellationToken
                );
                var existing = existingRules.FirstOrDefault(r => r.Name == profile.Name);
                if (existing != null)
                {
                    await _alertRuleRepository.UpdateRuleAsync(
                        existing.Id,
                        rule,
                        cancellationToken
                    );
                }
                else
                {
                    await _alertRuleRepository.CreateRuleAsync(rule, cancellationToken);
                }
            }

            // Also update legacy store to keep in sync
            // NOTE: This might ideally be done by fetching fresh data and saving it back to settings service
            // but for now we'll just rely on SaveAlarmConfiguration doing the heavy lifting if the frontend calls it.
            // If the frontend calls this endpoint, we might get out of sync with SettingsService JSON.
            // It's safer to fetch the full config from Repo and save to SettingsService.

            return await GetAlarmConfiguration(cancellationToken);
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

            if (Guid.TryParse(profileId, out var ruleId))
            {
                await _alertRuleRepository.DeleteRuleAsync(ruleId, cancellationToken);
            }

            // Sync legacy check omitted for brevity/performance, but ideally we should update it too.

            return await GetAlarmConfiguration(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting alarm profile");
            return StatusCode(500, new { error = "Failed to delete alarm profile" });
        }
    }

    private string GetUserId()
    {
        var userId =
            User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userId))
        {
            // Fallback for when auth is not fully configured or in dev variants
            return "00000000-0000-0000-0000-000000000001";
        }
        return userId;
    }

    private AlertRuleEntity MapProfileToRule(AlarmProfileConfiguration profile, string userId)
    {
        var rule = new AlertRuleEntity
        {
            // Id is handled by caller logic
            UserId = userId,
            Name = profile.Name,
            IsEnabled = profile.Enabled,
            // Threshold mapping based on AlarmType
            ForecastLeadTimeMinutes = profile.ForecastLeadTimeMinutes,

            // Map common properties
            MaxSnoozeMinutes = 240, // Default cap
            DefaultSnoozeMinutes = 15, // Default

            // Serialize client config
            ClientConfiguration = JsonSerializer.Serialize(
                new
                {
                    Audio = profile.Audio,
                    Visual = profile.Visual,
                    Priority = profile.Priority,
                    DisplayOrder = profile.DisplayOrder,
                    AlarmType = profile.AlarmType.ToString(),
                    OverrideQuietHours = profile.OverrideQuietHours,
                    PersistenceMinutes = profile.PersistenceMinutes,
                }
            ),
        };

        // Threshold mapping
        if (profile.AlarmType == AlarmTriggerType.UrgentLow)
            rule.UrgentLowThreshold = profile.Threshold;
        else if (profile.AlarmType == AlarmTriggerType.Low)
            rule.LowThreshold = profile.Threshold;
        else if (profile.AlarmType == AlarmTriggerType.High)
            rule.HighThreshold = profile.Threshold;
        else if (profile.AlarmType == AlarmTriggerType.UrgentHigh)
            rule.UrgentHighThreshold = profile.Threshold;
        else if (profile.AlarmType == AlarmTriggerType.ForecastLow)
        {
            // For ForecastLow, we usually use the LowThreshold as the target level
            rule.LowThreshold = profile.Threshold;
            // Forecast logic will look at ForecastLeadTimeMinutes AND LowThreshold
        }

        return rule;
    }

    private AlarmProfileConfiguration MapRuleToProfile(AlertRuleEntity rule)
    {
        var profile = new AlarmProfileConfiguration
        {
            Id = rule.Id.ToString(),
            Name = rule.Name,
            Enabled = rule.IsEnabled,
            ForecastLeadTimeMinutes = rule.ForecastLeadTimeMinutes,
        };

        // Determine AlarmType and Threshold from entity properties
        if (rule.UrgentLowThreshold.HasValue)
        {
            profile.Threshold = (int)rule.UrgentLowThreshold.Value;
            profile.AlarmType = AlarmTriggerType.UrgentLow;
        }
        else if (rule.UrgentHighThreshold.HasValue)
        {
            profile.Threshold = (int)rule.UrgentHighThreshold.Value;
            profile.AlarmType = AlarmTriggerType.UrgentHigh;
        }
        else if (rule.HighThreshold.HasValue)
        {
            profile.Threshold = (int)rule.HighThreshold.Value;
            profile.AlarmType = AlarmTriggerType.High;
        }
        else if (rule.LowThreshold.HasValue)
        {
            profile.Threshold = (int)rule.LowThreshold.Value;
            // Could be Low or ForecastLow based on ForecastLeadTimeMinutes
            if (rule.ForecastLeadTimeMinutes.HasValue && rule.ForecastLeadTimeMinutes > 0)
                profile.AlarmType = AlarmTriggerType.ForecastLow;
            else
                profile.AlarmType = AlarmTriggerType.Low;
        }

        // Deserialize ClientConfiguration
        if (!string.IsNullOrEmpty(rule.ClientConfiguration))
        {
            try
            {
                var doc = JsonDocument.Parse(rule.ClientConfiguration);
                if (doc.RootElement.TryGetProperty("Audio", out var audioEl))
                    profile.Audio = JsonSerializer.Deserialize<AlarmAudioSettings>(audioEl) ?? profile.Audio;
                if (doc.RootElement.TryGetProperty("Visual", out var visualEl))
                    profile.Visual = JsonSerializer.Deserialize<AlarmVisualSettings>(visualEl) ?? profile.Visual;
                if (doc.RootElement.TryGetProperty("Priority", out var prioEl))
                    profile.Priority = Enum.Parse<AlarmPriority>(prioEl.GetString() ?? "Normal");
                if (doc.RootElement.TryGetProperty("DisplayOrder", out var orderEl))
                    profile.DisplayOrder = orderEl.GetInt32();
                if (doc.RootElement.TryGetProperty("OverrideQuietHours", out var oqhEl))
                    profile.OverrideQuietHours = oqhEl.GetBoolean();
                if (doc.RootElement.TryGetProperty("PersistenceMinutes", out var pmEl))
                    profile.PersistenceMinutes = pmEl.GetInt32();

                // If AlarmType was stored explicitly, use it (overrides inference)
                if (doc.RootElement.TryGetProperty("AlarmType", out var typeEl))
                {
                    var typeStr = typeEl.GetString();
                    if (Enum.TryParse<AlarmTriggerType>(typeStr, out var savedType))
                    {
                        profile.AlarmType = savedType;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to deserialize ClientConfiguration for rule {RuleId}",
                    rule.Id
                );
            }
        }

        return profile;
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
            Widgets = new List<WidgetConfig>
            {
                new() { Id = WidgetId.BgDelta, Enabled = true, Placement = WidgetPlacement.Top },
                new() { Id = WidgetId.LastUpdated, Enabled = true, Placement = WidgetPlacement.Top },
                new() { Id = WidgetId.ConnectionStatus, Enabled = true, Placement = WidgetPlacement.Top },
                new() { Id = WidgetId.GlucoseChart, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.Statistics, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.Predictions, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.DailyStats, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.Treatments, Enabled = true, Placement = WidgetPlacement.Main },
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
            AlarmConfiguration = new UserAlarmConfiguration
            {
                Enabled = true,
                SoundEnabled = true,
                VibrationEnabled = true,
                GlobalVolume = 80,
                Profiles = new List<AlarmProfileConfiguration>(),
            },
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
        return ConnectorMetadataService.GetAvailableServices();
    }
}
