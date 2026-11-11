using Nocturne.Core.Contracts.Profiles.Resolvers;
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
/// leave such a temp out of delivery rather than read it as a suspension. A rate taken from the
/// schedule follows later writes to it (<see cref="TempBasalRateReresolver"/>).
/// </remarks>
public sealed class RelativeTempBasalRates(IBasalRateResolver basalRates)
{
    public async Task ResolveAsync(IEnumerable<TempBasal> relative, CancellationToken ct)
    {
        var unresolved = relative.Where(t => t is { Percent: not null, Rate: null }).ToList();
        foreach (var (tempBasal, (rate, scheduled)) in unresolved.Zip(await RatesAtAsync(unresolved, ct)))
        {
            tempBasal.Rate = rate;
            tempBasal.ScheduledRate = scheduled;
            tempBasal.RateFromSchedule = rate is not null;
        }
    }

    /// <summary>
    /// The rate each of <paramref name="tempBasals"/>' percent sets at its start and the scheduled rate
    /// it applies to, in order; both null where no schedule is in force. One read of the schedules for
    /// all of them.
    /// </summary>
    public async Task<IReadOnlyList<(double? Rate, double? ScheduledRate)>> RatesAtAsync(
        IReadOnlyList<TempBasal> tempBasals, CancellationToken ct)
    {
        if (tempBasals.Count == 0)
            return [];

        var scheduled = await basalRates.FindBasalRatesAsync([.. tempBasals.Select(t => t.StartMills)], ct);
        return [.. tempBasals.Zip(scheduled, (tempBasal, rate) => rate is { } value
            ? (TempBasal.RateRelativeTo(value, tempBasal.Percent!.Value), rate)
            : ((double?)null, (double?)null))];
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
}
