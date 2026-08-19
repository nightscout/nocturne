using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// Persists per-tenant UI settings as JSON blobs in the settings table, one row per section
/// (devices, algorithm, features, notifications, services, data quality, security) plus one row for
/// the alarm configuration.
/// </summary>
/// <remarks>
/// Every value has exactly one owning row. The aggregate <see cref="UISettingsConfiguration"/> is
/// assembled on read from the section rows rather than stored, and the alarm configuration lives
/// only under <c>ui:settings:notifications:alarms</c> — the notifications row is written without its
/// <see cref="NotificationSettings.AlarmConfiguration"/> so no second copy can go stale.
/// <c>ui:settings:complete</c>, written by earlier versions, is read-only: it is the fallback for a
/// section that has no row of its own, which is how tenants whose settings predate this layout keep
/// reading correctly.
/// </remarks>
/// <seealso cref="IUISettingsService"/>
public class UISettingsService : IUISettingsService
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<UISettingsService> _logger;
    private readonly IConfiguration _configuration;

    private const string SettingsKeyPrefix = "ui:settings:";
    private const string LegacyAggregateKey = "ui:settings:complete";
    private const string AlarmConfigurationKey = "ui:settings:notifications:alarms";
    private const string AlarmConfigurationNotes = "xDrip+-style alarm profiles configuration";
    private const string NotificationsSectionName = "notifications";

    /// <summary>
    /// A section of <see cref="UISettingsConfiguration"/>, its settings-table row and its property
    /// name in the persisted JSON (also the suffix of its key, see <see cref="GetSectionKey"/>).
    /// </summary>
    private sealed record Section(
        string Name,
        Type Type,
        Func<UISettingsConfiguration, object?> Get,
        Action<UISettingsConfiguration, object> Set
    )
    {
        public string Key => GetSectionKey(Name);
    }

    private static readonly Section[] Sections =
    [
        new(
            "devices",
            typeof(DeviceSettings),
            s => s.Devices,
            (s, v) => s.Devices = (DeviceSettings)v
        ),
        new(
            "algorithm",
            typeof(AlgorithmSettings),
            s => s.Algorithm,
            (s, v) => s.Algorithm = (AlgorithmSettings)v
        ),
        new(
            "features",
            typeof(FeatureSettings),
            s => s.Features,
            (s, v) => s.Features = (FeatureSettings)v
        ),
        new(
            NotificationsSectionName,
            typeof(NotificationSettings),
            s => s.Notifications,
            (s, v) => s.Notifications = (NotificationSettings)v
        ),
        new(
            "services",
            typeof(ServicesSettings),
            s => s.Services,
            (s, v) => s.Services = (ServicesSettings)v
        ),
        new(
            "dataQuality",
            typeof(DataQualitySettings),
            s => s.DataQuality,
            (s, v) => s.DataQuality = (DataQualitySettings)v
        ),
        new(
            "security",
            typeof(SecuritySettings),
            s => s.Security,
            (s, v) => s.Security = (SecuritySettings)v
        ),
    ];

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
            var stored = await ReadAllAsync(cancellationToken);
            var legacy = Value(stored, LegacyAggregateKey);
            var legacyAggregate = legacy == null ? null : JsonNode.Parse(legacy) as JsonObject;
            var settings = new UISettingsConfiguration();

            foreach (var section in Sections)
            {
                var value =
                    Deserialize(Value(stored, section.Key), section.Type)
                    ?? legacyAggregate?[section.Name]?.Deserialize(section.Type, JsonOptions);

                if (value != null)
                {
                    section.Set(settings, value);
                }
            }

            settings.Notifications.AlarmConfiguration =
                Deserialize(Value(stored, AlarmConfigurationKey), typeof(UserAlarmConfiguration))
                    as UserAlarmConfiguration
                ?? settings.Notifications.AlarmConfiguration;

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving UI settings");
            return new UISettingsConfiguration();
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
            foreach (var section in Sections)
            {
                var value = section.Get(settings);
                if (value != null)
                {
                    await WriteSectionAsync(section.Name, value, cancellationToken);
                }
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
            if (key == AlarmConfigurationKey)
            {
                return await GetAlarmConfigurationAsync(cancellationToken) as T;
            }

            if (FindSection(sectionName) is { } section)
            {
                return section.Get(await GetSettingsAsync(cancellationToken)) as T;
            }

            return await ReadAsync(key, typeof(T), cancellationToken) as T;
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
        try
        {
            await WriteSectionAsync(sectionName, sectionSettings, cancellationToken);

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
            NotificationsSectionName,
            cancellationToken
        );

        return section ?? new NotificationSettings();
    }

    /// <inheritdoc />
    public async Task<NotificationSettings> SaveNotificationSettingsAsync(
        NotificationSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        return await SaveSectionAsync(NotificationsSectionName, settings, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UserAlarmConfiguration?> GetAlarmConfigurationAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await ReadAlarmConfigurationAsync(cancellationToken)
                ?? (await GetNotificationSettingsAsync(cancellationToken)).AlarmConfiguration;
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
            await UpsertAsync(
                AlarmConfigurationKey,
                JsonSerializer.Serialize(config, JsonOptions),
                AlarmConfigurationNotes,
                cancellationToken
            );

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
            "alarms" or "alarmconfiguration" => AlarmConfigurationKey,
            var name => SettingsKeyPrefix + name,
        };
    }

    private static Section? FindSection(string sectionName)
    {
        return Sections.FirstOrDefault(s =>
            string.Equals(s.Name, sectionName, StringComparison.OrdinalIgnoreCase)
        );
    }

    private async Task<UserAlarmConfiguration?> ReadAlarmConfigurationAsync(
        CancellationToken cancellationToken
    )
    {
        return await ReadAsync(
            AlarmConfigurationKey,
            typeof(UserAlarmConfiguration),
            cancellationToken
        ) as UserAlarmConfiguration;
    }

    private async Task<object?> ReadAsync(string key, Type type, CancellationToken cancellationToken)
    {
        return Deserialize(await ReadValueAsync(key, cancellationToken), type);
    }

    private static object? Deserialize(string? value, Type type)
    {
        return value == null ? null : JsonSerializer.Deserialize(value, type, JsonOptions);
    }

    private static string? Value(IReadOnlyDictionary<string, string> stored, string key)
    {
        return stored.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Every stored UI settings row in one query, so assembling the aggregate does not cost a
    /// round trip per section.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadAllAsync(
        CancellationToken cancellationToken
    )
    {
        var rows = await _context
            .Settings.Where(s => s.IsActive && s.Key.StartsWith(SettingsKeyPrefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(cancellationToken);

        return rows.Where(r => r.Value != null)
            .GroupBy(r => r.Key)
            .ToDictionary(g => g.Key, g => g.First().Value!);
    }

    private async Task<string?> ReadValueAsync(string key, CancellationToken cancellationToken)
    {
        var entity = await _context.Settings.FirstOrDefaultAsync(
            s => s.Key == key && s.IsActive,
            cancellationToken
        );

        return entity?.Value;
    }

    /// <summary>
    /// Stages the row owning <paramref name="sectionSettings"/>. The notifications section splits in
    /// two: its alarm configuration goes to the row that owns it, and the section row is written
    /// without that property.
    /// </summary>
    private async Task WriteSectionAsync(
        string sectionName,
        object sectionSettings,
        CancellationToken cancellationToken
    )
    {
        var notes = $"UI settings section: {sectionName}";

        if (sectionSettings is NotificationSettings notifications)
        {
            await UpsertAsync(
                AlarmConfigurationKey,
                JsonSerializer.Serialize(notifications.AlarmConfiguration, JsonOptions),
                AlarmConfigurationNotes,
                cancellationToken
            );

            var node = JsonSerializer.SerializeToNode(notifications, JsonOptions)!.AsObject();
            node.Remove("alarmConfiguration");

            await UpsertAsync(
                GetSectionKey(sectionName),
                node.ToJsonString(),
                notes,
                cancellationToken
            );
            return;
        }

        await UpsertAsync(
            GetSectionKey(sectionName),
            JsonSerializer.Serialize(sectionSettings, JsonOptions),
            notes,
            cancellationToken
        );
    }

    private async Task UpsertAsync(
        string key,
        string value,
        string notes,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;

        var entity = await _context.Settings.FirstOrDefaultAsync(
            s => s.Key == key,
            cancellationToken
        );

        if (entity == null)
        {
            _context.Settings.Add(
                new SettingsEntity
                {
                    Id = Guid.CreateVersion7(),
                    Key = key,
                    Value = value,
                    Mills = now.ToUnixTimeMilliseconds(),
                    SrvCreated = now,
                    SrvModified = now,
                    IsActive = true,
                    Notes = notes,
                    App = "nocturne-api",
                }
            );
            return;
        }

        entity.Value = value;
        entity.SrvModified = now;
        entity.Mills = now.ToUnixTimeMilliseconds();
        // Reads filter on IsActive, so a deactivated row would swallow the write.
        entity.IsActive = true;
    }
}
