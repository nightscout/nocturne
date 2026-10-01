using Nocturne.Core.Models.Configuration;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Service interface for managing global MyFitnessPal matching settings.
/// </summary>
public interface IMyFitnessPalMatchingSettingsService
{
    /// <summary>
    /// Get current global MyFitnessPal matching settings. A tenant that has saved none reads back
    /// defaults, so null means the read itself failed.
    /// </summary>
    /// <returns>The matching settings, or null if they could not be read</returns>
    Task<MyFitnessPalMatchingSettings?> GetSettingsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Save global MyFitnessPal matching settings.
    /// </summary>
    Task<MyFitnessPalMatchingSettings> SaveSettingsAsync(
        MyFitnessPalMatchingSettings settings,
        CancellationToken cancellationToken = default
    );
}
