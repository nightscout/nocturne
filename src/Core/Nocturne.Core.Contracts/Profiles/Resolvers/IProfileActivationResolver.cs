using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// The one reader of which <see cref="ProfileActivation"/> applies when, and where its schedules come
/// from. Every profile resolver reads through it.
/// </summary>
public interface IProfileActivationResolver
{
    /// <summary>The activation that applies at <paramref name="timeMills"/>, or null for the profile store's default.</summary>
    Task<ResolvedProfileActivation?> GetAtAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// The stretches of [<paramref name="fromMills"/>, <paramref name="toMills"/>) an activation applies
    /// over, oldest first, without overlap. A stretch no activation covers is left out.
    /// </summary>
    Task<IReadOnlyList<ResolvedProfileSegment>> GetRangeAsync(
        long fromMills, long toMills, CancellationToken ct = default);
}

/// <summary>Where a resolved activation's schedules come from.</summary>
public enum ProfileScheduleSource
{
    /// <summary>The snapshot it was uploaded with, which already has its percentage and timeshift applied.</summary>
    CustomizedSnapshot,

    /// <summary>The stored profile it names, scaled by its percentage and shifted by its timeshift.</summary>
    ProfileStore,

    /// <summary>
    /// The base profile it was uploaded with, scaled and shifted, because the store lacks it; or the base
    /// profile as it stood when a person switched to it in Nocturne, so a later edit leaves that switch alone.
    /// </summary>
    BaseSnapshot,
}

/// <summary>A <see cref="ProfileActivation"/> as the profile resolvers apply it.</summary>
/// <param name="ProfileName">The stored profile the activation names.</param>
/// <param name="Schedules">
/// The schedules to read in place of the store's, for <see cref="ProfileScheduleSource.CustomizedSnapshot"/>
/// and <see cref="ProfileScheduleSource.BaseSnapshot"/>.
/// </param>
/// <param name="Scaling">
/// The percentage and timeshift to apply to the schedules: none for a customized snapshot, which has
/// them already.
/// </param>
public sealed record ResolvedProfileActivation(
    ProfileActivation Activation,
    string ProfileName,
    ProfileScheduleSource Source,
    ProfileActivationSnapshot? Schedules,
    CircadianAdjustment? Scaling);

/// <summary>A stretch of time one <see cref="ResolvedProfileActivation"/> applies over.</summary>
public sealed record ResolvedProfileSegment(long StartMills, long EndMills, ResolvedProfileActivation Active);
