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

    /// <summary>
    /// Returns the current effective ISF as a percentage of the schedule baseline.
    /// 100 means "at baseline"; values below 100 indicate the active CCP makes the
    /// pump more aggressive (lower ISF, more insulin per mg/dL); values above 100
    /// indicate it eases off. Returns <c>null</c> when no CCP adjustment is active.
    /// </summary>
    Task<double?> GetCurrentSensitivityPercentAsync(CancellationToken ct = default);
}
