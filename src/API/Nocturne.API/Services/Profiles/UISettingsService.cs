using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Helpers;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// Persists per-tenant UI settings as JSON blobs in the settings table, one row per
/// <see cref="UISettingsSections"/> entry plus one row for the alarm configuration.
/// </summary>
/// <remarks>
/// Every value has exactly one owning row. The aggregate <see cref="UISettingsConfiguration"/> is
/// assembled on read from the section rows rather than stored, and the alarm configuration lives
/// only under <c>ui:settings:notifications:alarms</c> — the notifications row is written without its
/// <see cref="NotificationSettings.AlarmConfiguration"/> so no second copy can go stale.
/// Anything a read serves but no tenant owns gets no row at all, per
/// <see cref="UISettingsSection.OmittedProperties"/>, so a client that sends it back cannot persist
/// it.
/// </remarks>
/// <seealso cref="IUISettingsService"/>
public class UISettingsService : IUISettingsService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly TimeProvider _time;
    private readonly ILogger<UISettingsService> _logger;

    private const string SettingsKeyPrefix = SettingsKeys.UiSettingsPrefix;
    private const string AlarmConfigurationKey = "ui:settings:notifications:alarms";
    private const string AlarmConfigurationNotes = "xDrip+-style alarm profiles configuration";

    /// <summary>First key of the advisory lock that serialises writes of one settings row.</summary>
    private const int SettingsRowLockClass = 0x5549_5350;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public UISettingsService(
        INocturneDbContextFactory contexts,
        TimeProvider timeProvider,
        ILogger<UISettingsService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UISettingsConfiguration?> GetSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        try
        {
            var stored = await ReadAllAsync(db, cancellationToken);
            var settings = new UISettingsConfiguration();

            foreach (var section in UISettingsSections.All)
            {
                var value = Deserialize(Value(stored, GetSectionKey(section.Name)), section.Type);

                if (value != null)
                {
                    section.Set(settings, value);
                }
            }

            var notifications = settings.Notifications ??= new NotificationSettings();
            notifications.AlarmConfiguration =
                Deserialize(Value(stored, AlarmConfigurationKey), typeof(UserAlarmConfiguration))
                    as UserAlarmConfiguration
                ?? notifications.AlarmConfiguration;

            settings.Features.Widgets = WidgetCatalog.MainSectionsOf(settings.Features.Widgets);

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving UI settings");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<UISettingsConfiguration> SaveSettingsAsync(
        UISettingsConfiguration settings,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        try
        {
            foreach (var section in UISettingsSections.All)
            {
                var value = section.Get(settings);
                if (value != null)
                {
                    await WriteSectionAsync(db, section.Name, value, cancellationToken);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
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

            if (UISettingsSections.Find(sectionName) is { } section)
            {
                return await GetSettingsAsync(cancellationToken) is { } settings
                    ? section.Get(settings) as T
                    : null;
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
    public async Task<T> PatchSectionAsync<T>(
        string sectionName,
        JsonElement patch,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        var section = PatchableSection<T>(sectionName, patch);
        EnsureFits(section, patch);

        var key = GetSectionKey(section.Name);
        await using var db = await _contexts.CreateAsync(cancellationToken);
        try
        {
            var patched = await db.ExecuteInTransactionAsync<object>(
                async token =>
                {
                    await LockRowAsync(db, key, token);
                    if (section.Type == typeof(NotificationSettings))
                    {
                        await LockRowAsync(db, AlarmConfigurationKey, token);
                    }

                    var stored = await ReadValueAsync(db, key, token);
                    var current = stored == null ? null : JsonNode.Parse(stored);
                    if (section.Type == typeof(NotificationSettings))
                    {
                        current = WithAlarmConfiguration(
                            current,
                            await ReadValueAsync(db, AlarmConfigurationKey, token)
                        );
                    }

                    var value = ApplyPatch(section, current, patch);

                    await WriteSectionAsync(db, section.Name, value, token);
                    await db.SaveChangesAsync(token);

                    return value;
                },
                ct: cancellationToken
            );

            _logger.LogInformation("Settings section {Section} patched successfully", section.Name);
            return (T)patched;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error patching settings section: {Section}", section.Name);
            throw;
        }
    }

    /// <inheritdoc />
    public T PreviewPatch<T>(string sectionName, T? current, JsonElement patch)
        where T : class
    {
        var section = PatchableSection<T>(sectionName, patch);
        EnsureFits(section, patch);

        return (T)ApplyPatch(section, current == null ? null : JsonSerializer.SerializeToNode(current, JsonOptions), patch);
    }

    private static UISettingsSection PatchableSection<T>(string sectionName, JsonElement patch)
    {
        var section =
            UISettingsSections.Find(sectionName)
            ?? throw new ArgumentException(
                $"Unknown UI settings section: {sectionName}",
                nameof(sectionName)
            );
        if (section.Type != typeof(T))
        {
            throw new ArgumentException(
                $"Section {section.Name} is a {section.Type.Name}, not a {typeof(T).Name}",
                nameof(sectionName)
            );
        }

        if (patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A section patch is a JSON object", nameof(patch));
        }

        return section;
    }

    /// <summary>
    /// Refuses a patch the section cannot hold, before any row is read, so a fault found later is
    /// the stored row's and not the caller's. A patch that reads as the section's type over an
    /// empty row reads as it over any row whose own properties read.
    /// </summary>
    /// <exception cref="ArgumentException">The patch names a key the section has no property for, or gives one a value of the wrong type.</exception>
    private static void EnsureFits(UISettingsSection section, JsonElement patch)
    {
        JsonMergePatch.EnsureNamesOf(section.Type, patch, JsonOptions);
        try
        {
            ApplyPatch(section, null, patch);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(ex.Message, nameof(patch), ex);
        }
    }

    /// <summary>
    /// <paramref name="current"/> with <paramref name="patch"/> merged over it, as the section's own
    /// type. Properties neither names take the type's defaults.
    /// </summary>
    /// <exception cref="JsonException">The merged section does not fit the section's type.</exception>
    private static object ApplyPatch(UISettingsSection section, JsonNode? current, JsonElement patch)
    {
        return JsonMergePatch.Merge(current, patch).Deserialize(section.Type, JsonOptions)!;
    }

    /// <summary>
    /// The notifications row with the alarm configuration its own row holds, which a patch of the
    /// notifications section merges into.
    /// </summary>
    private static JsonNode? WithAlarmConfiguration(JsonNode? notifications, string? alarmRow)
    {
        if (alarmRow == null)
        {
            return notifications;
        }

        var node = notifications as JsonObject ?? new JsonObject();
        node["alarmConfiguration"] = JsonNode.Parse(alarmRow);
        return node;
    }

    /// <inheritdoc />
    public async Task<NotificationSettings> GetNotificationSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var section = await GetSectionAsync<NotificationSettings>(
            UISettingsSections.Notifications,
            cancellationToken
        );

        return section ?? new NotificationSettings();
    }

    /// <inheritdoc />
    public async Task<UserAlarmConfiguration?> GetAlarmConfigurationAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var db = await _contexts.CreateAsync(cancellationToken);
            return await ReadAlarmConfigurationAsync(db, cancellationToken);
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
        var saved = (await ChangeAlarmConfigurationAsync(_ => config, cancellationToken))!;
        _logger.LogInformation(
            "Alarm configuration saved with {ProfileCount} profiles",
            saved.Profiles?.Count ?? 0
        );

        return saved;
    }

    /// <inheritdoc />
    public async Task<UserAlarmConfiguration> AddOrUpdateAlarmProfileAsync(
        AlarmProfileConfiguration profile,
        CancellationToken cancellationToken = default
    )
    {
        // A blank id would match every other blank-id profile on the next upsert, so the
        // list could never hold more than one of them.
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.CreateVersion7().ToString();
        }

        return (await ChangeAlarmConfigurationAsync(
            config =>
            {
                config.Profiles ??= [];

                var existing = config.Profiles.FindIndex(p => p.Id == profile.Id);
                if (existing >= 0)
                {
                    config.Profiles[existing] = profile;
                }
                else
                {
                    config.Profiles.Add(profile);
                }

                return config;
            },
            cancellationToken
        ))!;
    }

    /// <inheritdoc />
    public Task<UserAlarmConfiguration?> DeleteAlarmProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default
    )
    {
        return ChangeAlarmConfigurationAsync(
            config =>
            {
                config.Profiles ??= [];
                return config.Profiles.RemoveAll(p => p.Id == profileId) == 0 ? null : config;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Reads the alarm configuration, applies <paramref name="change"/> and writes the result, with
    /// the alarm row locked from the read to the commit, so concurrent changes and patches of the
    /// notifications section apply one after another.
    /// </summary>
    /// <param name="change">The configuration to write, or null to write nothing.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What <paramref name="change"/> returned.</returns>
    private async Task<UserAlarmConfiguration?> ChangeAlarmConfigurationAsync(
        Func<UserAlarmConfiguration, UserAlarmConfiguration?> change,
        CancellationToken cancellationToken
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        try
        {
            return await db.ExecuteInTransactionAsync(
                async token =>
                {
                    await LockRowAsync(db, AlarmConfigurationKey, token);

                    var changed = change(await ReadAlarmConfigurationAsync(db, token));
                    if (changed == null)
                    {
                        return null;
                    }

                    await UpsertAsync(
                        db,
                        AlarmConfigurationKey,
                        JsonSerializer.Serialize(changed, JsonOptions),
                        AlarmConfigurationNotes,
                        token
                    );
                    await db.SaveChangesAsync(token);

                    return changed;
                },
                ct: cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing alarm configuration");
            throw;
        }
    }

    /// <summary>
    /// Serialises transactions on one settings row. An advisory lock, not a row lock, because a row
    /// nobody has saved yet has nothing to lock.
    /// </summary>
    private static Task LockRowAsync(
        NocturneDbContext db,
        string key,
        CancellationToken cancellationToken
    )
    {
        return db.LockForTransactionAsync(
            SettingsRowLockClass,
            $"{db.TenantId}|{key}",
            cancellationToken
        );
    }

    /// <summary>
    /// The alarm row, else the copy an earlier layout left in the notifications row, else an empty
    /// configuration. A row carrying an explicit JSON null binds over the property initialiser, so
    /// a read that succeeded can still find nothing; null is reserved for a failed read.
    /// </summary>
    private async Task<UserAlarmConfiguration> ReadAlarmConfigurationAsync(
        NocturneDbContext db,
        CancellationToken cancellationToken
    )
    {
        var stored =
            Deserialize(
                await ReadValueAsync(db, AlarmConfigurationKey, cancellationToken),
                typeof(UserAlarmConfiguration)
            ) as UserAlarmConfiguration;

        if (stored == null
            && Deserialize(
                await ReadValueAsync(db, GetSectionKey(UISettingsSections.Notifications), cancellationToken),
                typeof(NotificationSettings)
            ) is NotificationSettings notifications)
        {
            stored = notifications.AlarmConfiguration;
        }

        return stored ?? new UserAlarmConfiguration();
    }

    /// <summary>
    /// The row key owning <paramref name="sectionName"/>. Lowercasing here is what makes
    /// <c>ui:settings:dataquality</c> the key of the <c>dataQuality</c> section; every read and write
    /// derives its key from this one function, so the two never disagree.
    /// </summary>
    private static string GetSectionKey(string sectionName)
    {
        return IsAlarmConfigurationName(sectionName)
            ? AlarmConfigurationKey
            : SettingsKeyPrefix + sectionName.ToLowerInvariant();
    }

    private static bool IsAlarmConfigurationName(string sectionName)
    {
        return sectionName.ToLowerInvariant() is "alarms" or "alarmconfiguration";
    }

    private async Task<object?> ReadAsync(string key, Type type, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return Deserialize(await ReadValueAsync(db, key, cancellationToken), type);
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
        NocturneDbContext db,
        CancellationToken cancellationToken
    )
    {
        var rows = await db
            .Settings.Where(s => s.IsActive && s.Key.StartsWith(SettingsKeyPrefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(cancellationToken);

        return rows.Where(r => r.Value != null)
            .GroupBy(r => r.Key)
            .ToDictionary(g => g.Key, g => g.First().Value!);
    }

    private async Task<string?> ReadValueAsync(NocturneDbContext db, string key, CancellationToken cancellationToken)
    {
        var entity = await db.Settings.FirstOrDefaultAsync(
            s => s.Key == key && s.IsActive,
            cancellationToken
        );

        return entity?.Value;
    }

    /// <summary>
    /// Stages the row owning <paramref name="sectionSettings"/>, minus whatever the section's
    /// <see cref="UISettingsSection.OmittedProperties"/> keeps off it. The notifications section
    /// splits in two, so its alarm configuration is additionally staged to the row that owns it.
    /// </summary>
    private async Task WriteSectionAsync(
        NocturneDbContext db,
        string sectionName,
        object sectionSettings,
        CancellationToken cancellationToken
    )
    {
        if (sectionSettings is NotificationSettings notifications)
        {
            await UpsertAsync(
                db,
                AlarmConfigurationKey,
                JsonSerializer.Serialize(notifications.AlarmConfiguration, JsonOptions),
                AlarmConfigurationNotes,
                cancellationToken
            );
        }

        await UpsertAsync(
            db,
            GetSectionKey(sectionName),
            SerializeForRow(sectionName, sectionSettings),
            $"UI settings section: {sectionName}",
            cancellationToken
        );
    }

    /// <summary>
    /// The JSON a section's row stores: the section as serialized, less the properties no row of
    /// this section carries.
    /// </summary>
    private static string SerializeForRow(string sectionName, object sectionSettings)
    {
        var omitted = UISettingsSections.Find(sectionName)?.OmittedProperties ?? [];

        if (omitted.Count == 0)
        {
            return JsonSerializer.Serialize(sectionSettings, JsonOptions);
        }

        var node = JsonSerializer.SerializeToNode(sectionSettings, JsonOptions)!.AsObject();

        foreach (var property in omitted)
        {
            node.Remove(property);
        }

        return node.ToJsonString();
    }

    private async Task UpsertAsync(
        NocturneDbContext db,
        string key,
        string value,
        string notes,
        CancellationToken cancellationToken
    )
    {
        var now = _time.GetUtcNow();

        var entity = await db.Settings.FirstOrDefaultAsync(
            s => s.Key == key,
            cancellationToken
        );

        if (entity == null)
        {
            db.Settings.Add(
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
