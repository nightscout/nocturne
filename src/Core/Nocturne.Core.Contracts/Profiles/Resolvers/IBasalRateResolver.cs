namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Resolves the scheduled basal rate (U/hr) at a given point in time,
/// accounting for profile switches and CircadianPercentageProfile adjustments.
/// </summary>
public interface IBasalRateResolver
{
    /// <summary>
    /// Returns the effective basal rate in U/hr at the given time,
    /// applying CCP percentage scaling when active.
    /// </summary>
    Task<double> GetBasalRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}
