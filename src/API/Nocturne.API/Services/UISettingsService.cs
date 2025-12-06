using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services;

/// <summary>
/// Service implementation for UI settings persistence.
/// Stores UI settings in the database using the Settings table with JSON values.
/// </summary>
public class UISettingsService : IUISettingsService
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<UISettingsService> _logger;
    private readonly IConfiguration _configuration;

    // Settings keys for different sections
    private const string UiSettingsKey = "ui:settings:complete";
    private const string DevicesSettingsKey = "ui:settings:devices";
    private const string AlgorithmSettingsKey = "ui:settings:algorithm";
    private const string FeaturesSettingsKey = "ui:settings:features";
    private const string NotificationsSettingsKey = "ui:settings:notifications";
    private const string ServicesSettingsKey = "ui:settings:services";
    private const string AlarmConfigurationKey = "ui:settings:notifications:alarms";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public UISettingsService(
        NocturneDbContext context,
        ILogger<UISettingsService> logger,
        IConfiguration configuration
    )
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<UISettingsConfiguration> GetSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == UiSettingsKey && s.IsActive,
                cancellationToken
            );

            if (entity?.Value != null)
            {
                var settings = JsonSerializer.Deserialize<UISettingsConfiguration>(
                    entity.Value,
                    JsonOptions
                );

                if (settings != null)
                {
                    return settings;
                }
            }

            // Return defaults if no saved settings
            return GenerateDefaultSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving UI settings");
            return GenerateDefaultSettings();
        }
    }

    /// <inheritdoc />
    public async Task<UISettingsConfiguration> SaveSettingsAsync(
        UISettingsConfiguration settings,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var jsonValue = JsonSerializer.Serialize(settings, JsonOptions);
            var now = DateTimeOffset.UtcNow;

            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == UiSettingsKey,
                cancellationToken
            );

            if (entity == null)
            {
                entity = new SettingsEntity
                {
                    Id = Guid.CreateVersion7(),
                    Key = UiSettingsKey,
                    Value = jsonValue,
                    Mills = now.ToUnixTimeMilliseconds(),
                    SrvCreated = now,
                    SrvModified = now,
                    IsActive = true,
                    Notes = "Complete UI settings configuration",
                    App = "nocturne-api",
                };
                _context.Settings.Add(entity);
            }
            else
            {
                entity.Value = jsonValue;
                entity.SrvModified = now;
                entity.Mills = now.ToUnixTimeMilliseconds();
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("UI settings saved successfully");

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving UI settings");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetSectionAsync<T>(
        string sectionName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        var key = GetSectionKey(sectionName);

        try
        {
            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == key && s.IsActive,
                cancellationToken
            );

            if (entity?.Value != null)
            {
                return JsonSerializer.Deserialize<T>(entity.Value, JsonOptions);
            }

            // Fall back to getting from complete settings
            var settings = await GetSettingsAsync(cancellationToken);
            return GetSectionFromSettings<T>(settings, sectionName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving settings section: {Section}", sectionName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<T> SaveSectionAsync<T>(
        string sectionName,
        T sectionSettings,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        var key = GetSectionKey(sectionName);

        try
        {
            var jsonValue = JsonSerializer.Serialize(sectionSettings, JsonOptions);
            var now = DateTimeOffset.UtcNow;

            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == key,
                cancellationToken
            );

            if (entity == null)
            {
                entity = new SettingsEntity
                {
                    Id = Guid.CreateVersion7(),
                    Key = key,
                    Value = jsonValue,
                    Mills = now.ToUnixTimeMilliseconds(),
                    SrvCreated = now,
                    SrvModified = now,
                    IsActive = true,
                    Notes = $"UI settings section: {sectionName}",
                    App = "nocturne-api",
                };
                _context.Settings.Add(entity);
            }
            else
            {
                entity.Value = jsonValue;
                entity.SrvModified = now;
                entity.Mills = now.ToUnixTimeMilliseconds();
            }

            // Also update the section in complete settings
            await UpdateSectionInCompleteSettings(sectionName, sectionSettings, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Settings section {Section} saved successfully", sectionName);

            return sectionSettings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings section: {Section}", sectionName);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<NotificationSettings> GetNotificationSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var section = await GetSectionAsync<NotificationSettings>(
            "notifications",
            cancellationToken
        );

        return section ?? GenerateDefaultNotificationSettings();
    }

    /// <inheritdoc />
    public async Task<NotificationSettings> SaveNotificationSettingsAsync(
        NotificationSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        return await SaveSectionAsync("notifications", settings, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UserAlarmConfiguration?> GetAlarmConfigurationAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // First try to get from dedicated alarm key
            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == AlarmConfigurationKey && s.IsActive,
                cancellationToken
            );

            if (entity?.Value != null)
            {
                return JsonSerializer.Deserialize<UserAlarmConfiguration>(
                    entity.Value,
                    JsonOptions
                );
            }

            // Fall back to notification settings
            var notifications = await GetNotificationSettingsAsync(cancellationToken);
            return notifications.AlarmConfiguration;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving alarm configuration");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<UserAlarmConfiguration> SaveAlarmConfigurationAsync(
        UserAlarmConfiguration config,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var jsonValue = JsonSerializer.Serialize(config, JsonOptions);
            var now = DateTimeOffset.UtcNow;

            // Save to dedicated alarm key
            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == AlarmConfigurationKey,
                cancellationToken
            );

            if (entity == null)
            {
                entity = new SettingsEntity
                {
                    Id = Guid.CreateVersion7(),
                    Key = AlarmConfigurationKey,
                    Value = jsonValue,
                    Mills = now.ToUnixTimeMilliseconds(),
                    SrvCreated = now,
                    SrvModified = now,
                    IsActive = true,
                    Notes = "xDrip+-style alarm profiles configuration",
                    App = "nocturne-api",
                };
                _context.Settings.Add(entity);
            }
            else
            {
                entity.Value = jsonValue;
                entity.SrvModified = now;
                entity.Mills = now.ToUnixTimeMilliseconds();
            }

            // Also update in notification settings
            var notifications = await GetNotificationSettingsAsync(cancellationToken);
            notifications.AlarmConfiguration = config;
            await SaveNotificationSettingsAsync(notifications, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Alarm configuration saved with {ProfileCount} profiles",
                config.Profiles?.Count ?? 0
            );

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving alarm configuration");
            throw;
        }
    }

    private static string GetSectionKey(string sectionName)
    {
        return sectionName.ToLowerInvariant() switch
        {
            "devices" => DevicesSettingsKey,
            "algorithm" => AlgorithmSettingsKey,
            "features" => FeaturesSettingsKey,
            "notifications" => NotificationsSettingsKey,
            "services" => ServicesSettingsKey,
            "alarms" or "alarmconfiguration" => AlarmConfigurationKey,
            _ => $"ui:settings:{sectionName.ToLowerInvariant()}",
        };
    }

    private static T? GetSectionFromSettings<T>(
        UISettingsConfiguration settings,
        string sectionName
    )
        where T : class
    {
        return sectionName.ToLowerInvariant() switch
        {
            "devices" => settings.Devices as T,
            "algorithm" => settings.Algorithm as T,
            "features" => settings.Features as T,
            "notifications" => settings.Notifications as T,
            "services" => settings.Services as T,
            _ => null,
        };
    }

    private async Task UpdateSectionInCompleteSettings<T>(
        string sectionName,
        T sectionSettings,
        CancellationToken cancellationToken
    )
        where T : class
    {
        var settings = await GetSettingsAsync(cancellationToken);

        switch (sectionName.ToLowerInvariant())
        {
            case "devices" when sectionSettings is DeviceSettings ds:
                settings.Devices = ds;
                break;
            case "algorithm" when sectionSettings is AlgorithmSettings alg:
                settings.Algorithm = alg;
                break;
            case "features" when sectionSettings is FeatureSettings fs:
                settings.Features = fs;
                break;
            case "notifications" when sectionSettings is NotificationSettings ns:
                settings.Notifications = ns;
                break;
            case "services" when sectionSettings is ServicesSettings ss:
                settings.Services = ss;
                break;
        }

        // Update the complete settings (without recursion)
        var jsonValue = JsonSerializer.Serialize(settings, JsonOptions);
        var now = DateTimeOffset.UtcNow;

        var entity = await _context.Settings.FirstOrDefaultAsync(
            s => s.Key == UiSettingsKey,
            cancellationToken
        );

        if (entity != null)
        {
            entity.Value = jsonValue;
            entity.SrvModified = now;
            entity.Mills = now.ToUnixTimeMilliseconds();
        }
    }

    private UISettingsConfiguration GenerateDefaultSettings()
    {
        return new UISettingsConfiguration
        {
            Devices = new DeviceSettings(),
            Algorithm = new AlgorithmSettings(),
            Features = GenerateDefaultFeatureSettings(),
            Notifications = GenerateDefaultNotificationSettings(),
            Services = new ServicesSettings(),
        };
    }

    private static FeatureSettings GenerateDefaultFeatureSettings()
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
            Plugins = new Dictionary<string, PluginSettings>(),
        };
    }

    private static NotificationSettings GenerateDefaultNotificationSettings()
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
}
