namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Resolves the blood glucose target range (low/high in mg/dL) at a given point in time,
/// accounting for profile switches. Target ranges are not adjusted by CCP.
/// </summary>
public interface ITargetRangeResolver
{
    /// <summary>
    /// Returns the low BG target in mg/dL at the given time.
    /// </summary>
    Task<double> GetLowBGTargetAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the high BG target in mg/dL at the given time.
    /// </summary>
    Task<double> GetHighBGTargetAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the low and high BG targets in mg/dL at the given time from a single resolution.
    /// </summary>
    Task<(double Low, double High)> GetBGTargetRangeAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}
