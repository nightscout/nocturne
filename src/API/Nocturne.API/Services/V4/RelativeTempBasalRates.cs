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
/// leave such a temp out of delivery rather than read it as a suspension.
/// </remarks>
public sealed class RelativeTempBasalRates(IBasalRateResolver basalRates)
{
    public async Task ResolveAsync(IEnumerable<TempBasal> tempBasals, CancellationToken ct)
    {
        foreach (var tempBasal in tempBasals)
        {
            if (tempBasal is not { Percent: { } percent, Rate: null }
                || await basalRates.FindBasalRateAsync(tempBasal.StartMills, ct) is not { } scheduled)
                continue;

            tempBasal.ScheduledRate = scheduled;
            tempBasal.Rate = TempBasal.RateRelativeTo(scheduled, percent);
        }
    }
}
