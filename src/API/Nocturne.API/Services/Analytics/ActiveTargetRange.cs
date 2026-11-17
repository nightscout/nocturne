using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

internal static class ActiveTargetRange
{
    /// <summary>
    /// The target range schedule in force now for the tenant's active profile, or null when none is
    /// configured. Report statistics and AID metrics read it as the alert engine and
    /// <c>TargetRangeResolver</c> do: the active profile's newest schedule at the instant.
    /// </summary>
    public static async Task<TargetRangeSchedule?> ScheduleAsync(
        IActiveProfileResolver activeProfileResolver,
        ITargetRangeScheduleRepository targetRangeScheduleRepository,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var profileName = await activeProfileResolver.GetProfileNameAsync(now.ToUnixTimeMilliseconds(), ct);
        // Before the store's first version, its newest, as ProfileVersionCache.ActiveAt reads it.
        return await targetRangeScheduleRepository.GetActiveAtAsync(profileName, now.UtcDateTime, ct)
            ?? (await targetRangeScheduleRepository.GetByProfileNameAsync(profileName, ct)).MaxBy(s => s.Timestamp);
    }
}
