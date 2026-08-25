using Nocturne.Core.Models.Configuration;

namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// Service interface for managing UI settings persistence.
/// Handles loading and saving UISettingsConfiguration to the database.
/// </summary>
public interface IUISettingsService
{
    /// <summary>
    /// Gets the complete UI settings configuration for the user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The UI settings configuration</returns>
    Task<UISettingsConfiguration> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the complete UI settings configuration.
    /// </summary>
    /// <param name="settings">The settings to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved settings</returns>
    Task<UISettingsConfiguration> SaveSettingsAsync(
        UISettingsConfiguration settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets a specific section of the UI settings.
    /// </summary>
    /// <typeparam name="T">The section type</typeparam>
    /// <param name="sectionName">The section name (e.g., "notifications", "devices")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The section settings</returns>
    Task<T?> GetSectionAsync<T>(string sectionName, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Saves a specific section of the UI settings.
    /// </summary>
    /// <typeparam name="T">The section type</typeparam>
    /// <param name="sectionName">
    /// A <see cref="UISettingsSections"/> name, or <c>alarms</c>/<c>alarmConfiguration</c> for the
    /// alarm configuration. Reads only ever look under those keys, so any other name would store a
    /// row nothing can serve.
    /// </param>
    /// <param name="sectionSettings">The section settings to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved section settings</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sectionName"/> names neither a section nor the alarm configuration.
    /// </exception>
    Task<T> SaveSectionAsync<T>(
        string sectionName,
        T sectionSettings,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>
    /// Gets the notification settings including alarm configuration.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The notification settings</returns>
    Task<NotificationSettings> GetNotificationSettingsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Saves the notification settings including alarm configuration.
    /// </summary>
    /// <param name="settings">The notification settings to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved notification settings</returns>
    Task<NotificationSettings> SaveNotificationSettingsAsync(
        NotificationSettings settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets just the alarm configuration from notification settings. A tenant that has never saved
    /// one reads back an empty configuration, so null means the read itself failed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The alarm configuration, or null if it could not be read</returns>
    Task<UserAlarmConfiguration?> GetAlarmConfigurationAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Saves just the alarm configuration within notification settings.
    /// </summary>
    /// <param name="config">The alarm configuration to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saved alarm configuration</returns>
    Task<UserAlarmConfiguration> SaveAlarmConfigurationAsync(
        UserAlarmConfiguration config,
        CancellationToken cancellationToken = default
    );
}
