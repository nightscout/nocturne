namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Resolves the insulin sensitivity factor (mg/dL per U) at a given point in time,
/// accounting for profile switches and CircadianPercentageProfile adjustments.
/// </summary>
public interface ISensitivityResolver
{
    /// <summary>
    /// Returns the effective ISF at the given time,
    /// applying inverse CCP percentage scaling when active.
    /// </summary>
    Task<double> GetSensitivityAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}
