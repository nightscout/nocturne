using System.Text.Json;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// Service interface for managing UI settings persistence.
/// Handles loading and saving UISettingsConfiguration to the database.
/// </summary>
public interface IUISettingsService
{
    /// <summary>
    /// Gets the complete UI settings configuration for the user. A tenant that has saved nothing
    /// reads back a configuration of defaults, so null means the read itself failed.
    /// </summary>
    /// <returns>The UI settings configuration, or null if it could not be read</returns>
    Task<UISettingsConfiguration?> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <returns>The saved settings</returns>
    Task<UISettingsConfiguration> SaveSettingsAsync(
        UISettingsConfiguration settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets a specific section of the UI settings.
    /// </summary>
    /// <param name="sectionName">The section name (e.g., "notifications", "devices")</param>
    Task<T?> GetSectionAsync<T>(string sectionName, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Applies an RFC 7386 merge-patch to a section: a present key replaces, <c>null</c> resets to
    /// the default, an absent key is untouched and a nested object merges. Patches of one section
    /// run one at a time, so two patches of different fields both land.
    /// </summary>
    /// <param name="sectionName">A <see cref="UISettingsSections"/> name.</param>
    /// <param name="patch">A JSON object.</param>
    /// <returns>The section as stored after the patch</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sectionName"/> names no section, <typeparamref name="T"/> is not its type,
    /// <paramref name="patch"/> is not an object, or it names a key that is not the JSON name of a
    /// property of the section (at any depth, exact case) or gives a property a value of the wrong type.
    /// </exception>
    /// <exception cref="JsonException">The stored row does not fit the section's type.</exception>
    Task<T> PatchSectionAsync<T>(
        string sectionName,
        JsonElement patch,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>
    /// The section as <see cref="PatchSectionAsync{T}"/> would return it with <paramref name="patch"/>
    /// merged over <paramref name="current"/>. Nothing is read or stored.
    /// </summary>
    /// <param name="current">The section to patch, or null for the section's defaults.</param>
    /// <exception cref="ArgumentException">As <see cref="PatchSectionAsync{T}"/>.</exception>
    T PreviewPatch<T>(string sectionName, T? current, JsonElement patch)
        where T : class;

    /// <summary>
    /// Gets the notification settings including alarm configuration.
    /// </summary>
    Task<NotificationSettings> GetNotificationSettingsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets just the alarm configuration from notification settings. A tenant that has never saved
    /// one reads back an empty configuration, so null means the read itself failed.
    /// </summary>
    /// <returns>The alarm configuration, or null if it could not be read</returns>
    Task<UserAlarmConfiguration?> GetAlarmConfigurationAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Saves just the alarm configuration within notification settings.
    /// </summary>
    /// <returns>The saved alarm configuration</returns>
    Task<UserAlarmConfiguration> SaveAlarmConfigurationAsync(
        UserAlarmConfiguration config,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Adds a profile to the alarm configuration, or replaces the one with the same id. A profile
    /// with no id is given one. Changes of the alarm configuration run one at a time, so two
    /// concurrent changes both land.
    /// </summary>
    /// <returns>The alarm configuration after the change</returns>
    Task<UserAlarmConfiguration> AddOrUpdateAlarmProfileAsync(
        AlarmProfileConfiguration profile,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Removes a profile from the alarm configuration.
    /// </summary>
    /// <returns>The alarm configuration after the change, or null if no profile has the id</returns>
    Task<UserAlarmConfiguration?> DeleteAlarmProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default
    );
}
