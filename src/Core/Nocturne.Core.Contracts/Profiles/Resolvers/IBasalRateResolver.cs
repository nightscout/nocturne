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

    /// <summary>
    /// Pre-fetches all profile data for [fromMs, toMs] in a fixed number of DB queries
    /// and returns a synchronous delegate that resolves any timestamp in that range without
    /// further DB access. Use this instead of calling GetBasalRateAsync in a loop.
    /// </summary>
    Task<Func<long, double>> BuildResolverAsync(long fromMs, long toMs, CancellationToken ct = default);
}
