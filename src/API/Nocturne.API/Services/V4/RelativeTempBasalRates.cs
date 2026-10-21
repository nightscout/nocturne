using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Gives a temp basal set relative to the schedule (<see cref="TempBasal.Percent"/>) that arrived
/// without a rate the rate it resolves to against the scheduled basal in force at its start, and
/// that scheduled rate.
/// </summary>
/// <remarks>
/// A rate the uploader sent is kept: AAPS converts against its own profile, profile switch
/// percentage included, and may upload the temp before the switch that set it. Where no schedule is
/// in force the rate stays null rather than taking the basal resolver's hardcoded default; readers
/// leave such a temp out of delivery rather than read it as a suspension, and
/// <see cref="ResolvePendingAsync"/> resolves it once a schedule covering it is written.
/// </remarks>
public sealed class RelativeTempBasalRates(
    IBasalRateResolver basalRates,
    ITempBasalRepository tempBasals,
    IBasalScheduleRepository basalSchedules,
    ProfileVersionCache versions)
{
    private const int PageSize = 500;

    public async Task ResolveAsync(IEnumerable<TempBasal> relative, CancellationToken ct)
    {
        foreach (var tempBasal in relative)
            await TryResolveAsync(tempBasal, ct);
    }

    /// <summary>
    /// Drops the rate a read-back carried into an edit that changed the percent, so the edit
    /// resolves anew rather than keep the rate of the old percent.
    /// </summary>
    public static void ForgetEchoedRate(TempBasal edited, TempBasal? stored)
    {
        if (stored is { Percent: { } storedPercent }
            && edited.Percent is { } percent
            && percent != storedPercent
            && edited.Rate == stored.Rate)
        {
            edited.Rate = null;
            edited.ScheduledRate = null;
        }
    }

    /// <summary>
    /// Resolves the stored temps whose rate is still unknown, after basal schedules of
    /// <paramref name="profileNames"/> were written. Idempotent: only rows with no rate are read, and
    /// one still uncovered stays as it is.
    /// </summary>
    public async Task<int> ResolvePendingAsync(IEnumerable<string> profileNames, CancellationToken ct)
    {
        foreach (var name in profileNames.Distinct(StringComparer.Ordinal))
            await versions.RefreshAsync(basalSchedules, name, ct);

        var resolved = 0;
        (DateTime, Guid)? after = null;
        while (true)
        {
            var page = await tempBasals.GetUnresolvedRelativeAsync(after, PageSize, ct);
            foreach (var tempBasal in page)
            {
                if (await TryResolveAsync(tempBasal, ct))
                {
                    await tempBasals.UpdateAsync(tempBasal.Id, tempBasal, WriteOrigin.Live, ct);
                    resolved++;
                }
            }

            if (page.Count < PageSize)
                return resolved;
            after = (page[^1].StartTimestamp, page[^1].Id);
        }
    }

    private async Task<bool> TryResolveAsync(TempBasal tempBasal, CancellationToken ct)
    {
        if (tempBasal is not { Percent: { } percent, Rate: null }
            || await basalRates.FindBasalRateAsync(tempBasal.StartMills, ct) is not { } scheduled)
            return false;

        tempBasal.ScheduledRate = scheduled;
        tempBasal.Rate = TempBasal.RateRelativeTo(scheduled, percent);
        tempBasal.RateFromSchedule = true;
        return true;
    }
}
