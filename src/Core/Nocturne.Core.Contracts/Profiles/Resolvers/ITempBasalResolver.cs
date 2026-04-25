namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Resolves the effective basal rate at a given time, combining the scheduled basal
/// with any active temporary basal override.
/// </summary>
public interface ITempBasalResolver
{
    /// <summary>
    /// Returns a <see cref="TempBasalResolverResult"/> describing the scheduled basal,
    /// any active temp basal, and the total effective basal rate.
    /// </summary>
    Task<TempBasalResolverResult> GetTempBasalAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}

/// <summary>
/// Result of temp basal resolution: scheduled basal, active temp basal override, and total effective rate.
/// </summary>
/// <param name="Basal">Scheduled basal rate (U/hr).</param>
/// <param name="TempBasal">Active temp basal rate (U/hr), or null if no temp basal is active.</param>
/// <param name="ComboBolusBasal">Extended combo bolus portion as basal (U/hr), or null if none active.</param>
/// <param name="TotalBasal">Total effective basal rate (U/hr).</param>
public record TempBasalResolverResult(double Basal, double? TempBasal, double? ComboBolusBasal, double TotalBasal);
