namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Resolves the insulin-to-carb ratio (g/U) at a given point in time,
/// accounting for profile switches and CircadianPercentageProfile adjustments.
/// </summary>
public interface ICarbRatioResolver
{
    /// <summary>
    /// Returns the effective carb ratio at the given time,
    /// applying inverse CCP percentage scaling when active.
    /// </summary>
    Task<double> GetCarbRatioAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}
