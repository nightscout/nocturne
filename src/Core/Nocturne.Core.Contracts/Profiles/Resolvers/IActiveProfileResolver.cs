using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// The active profile at a point in time, and how its schedules are adjusted, as
/// <see cref="IProfileActivationResolver"/> resolves it.
/// </summary>
public interface IActiveProfileResolver
{
    /// <summary>
    /// Returns the name of the stored profile active at the given time, or null if no profile
    /// activation applies, when <see cref="GetDefaultProfileNameAsync"/> is the one in force.
    /// </summary>
    Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// Returns the name of the stored profile in force when no profile activation applies: the
    /// tenant's default store, or "Default" when it has none.
    /// </summary>
    Task<string> GetDefaultProfileNameAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the percentage and timeshift to apply to the active profile's schedules at the given
    /// time, or null when there are none to apply: no activation, or one whose schedules already
    /// have them applied.
    /// </summary>
    Task<CircadianAdjustment?> GetCircadianAdjustmentAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// Returns the schedules to read in place of the stored profile's at the given time, or null when
    /// they come from the store; see <see cref="ResolvedProfileActivation.Schedules"/>.
    /// </summary>
    Task<ProfileActivationSnapshot?> GetActiveSchedulesAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// Returns the stretches of [fromMs, toMs] a profile activation applies over, in chronological
    /// order without overlap. Used by batch resolvers to avoid per-timestamp DB queries.
    /// </summary>
    Task<IReadOnlyList<ProfileSpan>> GetActiveProfileSpansForRangeAsync(
        long fromMs, long toMs, CancellationToken ct = default);

    /// <summary>
    /// Returns the insulin pharmacokinetic configuration the profile activation active at the given
    /// time was uploaded with, or null if it carried none (e.g., non-AAPS source).
    /// </summary>
    Task<TreatmentInsulinContext?> GetActiveInsulinContextAsync(long timeMills, CancellationToken ct = default);
}

/// <summary>
/// A percentage and timeshift to apply to a profile's schedules.
/// </summary>
/// <param name="Percentage">Basal rate percentage (100 = no change).</param>
/// <param name="TimeshiftMs">Time shift in milliseconds applied to the schedule.</param>
public record CircadianAdjustment(double Percentage, long TimeshiftMs);

/// <summary>
/// A stretch of time one profile activation applies over, for batch resolution.
/// </summary>
/// <param name="Schedules">The schedules to read in place of the store's; see <see cref="ResolvedProfileActivation.Schedules"/>.</param>
public record ProfileSpan(
    string ProfileName,
    long StartMills,
    long? EndMills,
    CircadianAdjustment? Adjustment,
    ProfileActivationSnapshot? Schedules = null);
