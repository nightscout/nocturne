using Nocturne.Core.Models.V4;

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

    /// <summary>
    /// Returns all profile spans that could be active during [fromMs, toMs] with metadata
    /// pre-extracted, in chronological order. Used by batch resolvers to avoid per-timestamp
    /// DB queries.
    /// </summary>
    Task<IReadOnlyList<ProfileSpan>> GetActiveProfileSpansForRangeAsync(
        long fromMs, long toMs, CancellationToken ct = default);

    /// <summary>
    /// Returns the insulin pharmacokinetic configuration from the profile switch active at the given time,
    /// or null if the active profile switch has no insulin metadata (e.g., non-AAPS source).
    /// </summary>
    Task<TreatmentInsulinContext?> GetActiveInsulinContextAsync(long timeMills, CancellationToken ct = default);
}

/// <summary>
/// CircadianPercentageProfile adjustment extracted from a Profile StateSpan's metadata.
/// </summary>
/// <param name="Percentage">Basal rate percentage (100 = no change).</param>
/// <param name="TimeshiftMs">Time shift in milliseconds applied to the schedule.</param>
public record CircadianAdjustment(double Percentage, long TimeshiftMs);

/// <summary>
/// A profile span with metadata pre-extracted for use in batch resolution.
/// </summary>
public record ProfileSpan(
    string ProfileName,
    long StartMills,
    long? EndMills,
    CircadianAdjustment? Adjustment);
