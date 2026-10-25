using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// The active profile at a point in time, and how its schedules are adjusted, as
/// <see cref="IProfileActivationResolver"/> resolves it.
/// </summary>
public interface IActiveProfileResolver
{
    /// <summary>
    /// Returns the name of the stored profile the activation applying at the given time names, or null
    /// if none applies. The store read then is <see cref="GetProfileNameAsync"/>.
    /// </summary>
    Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// Returns the name of the stored profile read at the given time: the one the activation applying
    /// then names, when the profile store in force holds it, else
    /// <see cref="GetDefaultProfileNameAsync"/>.
    /// </summary>
    Task<string> GetProfileNameAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// Returns the name of the default store of the profile store in force at the given time, or
    /// "Default" when there is no stored profile.
    /// </summary>
    Task<string> GetDefaultProfileNameAsync(long timeMills, CancellationToken ct = default);

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
    /// Returns [fromMs, toMs) cut wherever the stored profile read (<see cref="GetProfileNameAsync"/>)
    /// or the activation applying changes, in chronological order without overlap or gap. An empty or
    /// inverted range is read as the instant <paramref name="fromMs"/>. Used by batch resolvers to avoid
    /// per-timestamp DB queries.
    /// </summary>
    Task<IReadOnlyList<ProfileSpan>> GetProfileSpansForRangeAsync(
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
public record CircadianAdjustment(double Percentage, long TimeshiftMs)
{
    /// <summary>The percentage and timeshift <paramref name="activation"/> names, the shift within one day.</summary>
    public static CircadianAdjustment Of(ProfileActivation activation) =>
        new(activation.Percentage, activation.TimeshiftMs % TimeSpan.MillisecondsPerDay);
}

/// <summary>
/// A stretch of time one stored profile is read over, under one profile activation or none, for batch
/// resolution.
/// </summary>
/// <param name="ProfileName">The stored profile read; see <see cref="IActiveProfileResolver.GetProfileNameAsync"/>.</param>
/// <param name="Adjustment">The activation's percentage and timeshift, or null; see <see cref="IActiveProfileResolver.GetCircadianAdjustmentAsync"/>.</param>
/// <param name="Schedules">The schedules to read in place of the store's; see <see cref="ResolvedProfileActivation.Schedules"/>.</param>
/// <param name="AppliedAdjustment">
/// The activation's percentage and timeshift when <paramref name="Schedules"/> already has them applied
/// (<see cref="ProfileScheduleSource.CustomizedSnapshot"/>), so they are not in <paramref name="Adjustment"/>.
/// </param>
public record ProfileSpan(
    string ProfileName,
    long StartMills,
    long? EndMills,
    CircadianAdjustment? Adjustment,
    ProfileActivationSnapshot? Schedules = null,
    CircadianAdjustment? AppliedAdjustment = null);
