namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Determines the active named profile and any CircadianPercentageProfile adjustments
/// at a given point in time by querying Profile StateSpans.
/// </summary>
public interface IActiveProfileResolver
{
    /// <summary>
    /// Returns the name of the active profile at the given time, or null if no profile
    /// switch is active (callers should fall back to "Default").
    /// </summary>
    Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// Returns the CircadianPercentageProfile adjustment active at the given time,
    /// or null if no CCP data is present in the active profile switch.
    /// </summary>
    Task<CircadianAdjustment?> GetCircadianAdjustmentAsync(long timeMills, CancellationToken ct = default);
}

/// <summary>
/// CircadianPercentageProfile adjustment extracted from a Profile StateSpan's metadata.
/// </summary>
/// <param name="Percentage">Basal rate percentage (100 = no change).</param>
/// <param name="TimeshiftMs">Time shift in milliseconds applied to the schedule.</param>
public record CircadianAdjustment(double Percentage, long TimeshiftMs);
