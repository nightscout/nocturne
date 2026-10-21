using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Gives a temp basal set relative to the schedule (<see cref="TempBasal.Percent"/>) the rate it
/// resolves to against the scheduled basal at its start, and that scheduled rate.
/// </summary>
/// <remarks>
/// With no therapy settings on file there is no schedule to resolve against, and the basal resolver
/// would answer its hardcoded default; the rate the upload carried, if any, is kept instead and
/// <see cref="TempBasal.ScheduledRate"/> stays unset.
/// </remarks>
public sealed class RelativeTempBasalRates(
    IBasalRateResolver basalRates,
    ITherapySettingsResolver therapySettings)
{
    public async Task ResolveAsync(IReadOnlyCollection<TempBasal> tempBasals, CancellationToken ct)
    {
        var relative = tempBasals.Where(t => t.Percent is not null).ToList();
        if (relative.Count == 0 || !await therapySettings.HasDataAsync(ct))
            return;

        var scheduledAt = await basalRates.BuildResolverAsync(
            relative.Min(t => t.StartMills), relative.Max(t => t.StartMills), ct);

        foreach (var tempBasal in relative)
        {
            var scheduled = scheduledAt(tempBasal.StartMills);
            tempBasal.ScheduledRate = scheduled;
            tempBasal.Rate = TempBasal.RateRelativeTo(scheduled, tempBasal.Percent!.Value);
        }
    }
}
