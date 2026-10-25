using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Clinical.Devices;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

internal sealed partial class LoopAnalysisService
{
    public async Task<LoopHealthResponse> GetLoopHealthAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var (primary, comparison, names) = await ForRangesAsync(
            filter,
            window => LoadLoopHealthAsync(window, zone, filter.Now.ToMills(), ct),
            (loaded, window) => CalculateLoopHealth(loaded, filter, window, zone));
        return new LoopHealthResponse { Primary = primary, Comparison = comparison, ProfileNames = names };
    }

    private sealed record LoopHealthLoaded(
        LoopHealthInputs Inputs,
        LoopAnalysisMaskSources Sources,
        IReadOnlyList<PatientDevice> Devices,
        DeviceDateClock DeviceDates,
        IReadOnlyList<TempBasal> TempBasals) : ILoaded;

    /// <remarks>
    /// The loop cycles are read from <see cref="LoopHealthCalculator.TempLookbackMs"/> and
    /// <see cref="LoopHealthCalculator.SetterWindowMs"/> before the window, so the cycle that set a
    /// temp running at its start is read, and the repository adds the nearest cycle beyond each end,
    /// so a loop gap that straddles either end is bounded.
    /// </remarks>
    private async Task<LoopHealthLoaded> LoadLoopHealthAsync(Window window, TimeZoneInfo zone, long now, CancellationToken ct)
    {
        var to = window.To;
        var cyclesTask = apsSnapshotRepository.GetLoopCyclesAsync(
            ToUtc(window.From - LoopHealthCalculator.TempLookbackMs - LoopHealthCalculator.SetterWindowMs), ToUtc(to), ct);
        var commonTask = LoadCommonAsync(window, ct);
        await Task.WhenAll(cyclesTask, commonTask);

        var cycles = await cyclesTask;
        var common = await commonTask;
        var delivery = await DeliveryAsync(common, window, now, ct);

        var inputs = new LoopHealthInputs
        {
            Zone = zone,
            Cycles = cycles,
            Insulin = delivery.Insulin,
            Evidence = delivery.Evidence,
            Temps = delivery.Temps,
            Glucose = common.Glucose,
            LoopMaximumBasalRate = cycles.Any(c => c.AidAlgorithm == AidAlgorithm.Loop)
                ? await LoopMaximumBasalRateAsync(common.Profiles, to, ct)
                : null,
        };

        return new LoopHealthLoaded(
            inputs,
            Sources(common, SnapshotMills(cycles.Select(c => c.Timestamp)), to),
            common.Devices,
            common.DeviceDates,
            common.TempBasals);
    }

    /// <summary>
    /// <see cref="LoopHealthCalculator"/> over the window, with the overall activity percentages from
    /// <see cref="AidSystemMetricsReader"/> over the same records, unmasked.
    /// </summary>
    private LoopHealthRange CalculateLoopHealth(
        LoopHealthLoaded loaded, LoopAnalysisFilter filter, Window window, TimeZoneInfo zone)
    {
        var inputs = loaded.Inputs;
        var range = LoopHealthCalculator.Calculate(
            inputs, Mask(filter, window, zone, loaded.Sources), window.From, window.To);

        var start = ToUtc(window.From);
        var end = ToUtc(window.To);
        var overall = aidSystemMetrics.Calculate(
            new AidSystemMetricsRecords(
                loaded.Devices,
                [.. inputs.Cycles.Where(c => c.Timestamp >= start && c.Timestamp < end)],
                loaded.TempBasals,
                [],
                [.. inputs.Glucose.Where(g => g.Mills >= window.From && g.Mills < window.To)],
                null,
                null),
            start,
            end,
            loaded.DeviceDates);
        range.Activity.AidActivePercent = overall.AidActivePercent;
        range.Activity.PumpUsePercent = overall.PumpUsePercent;
        range.Activity.CgmActivePercent = overall.CgmActivePercent;
        return range;
    }

    /// <summary>
    /// Loop's maximum basal rate setting over time: the version of each active profile's settings
    /// in force, read once per profile, where it records one.
    /// </summary>
    private async Task<StepTimeline<double>> LoopMaximumBasalRateAsync(
        IReadOnlyList<ProfileSpan> profiles, long to, CancellationToken ct)
    {
        var versions = await VersionsByProfileAsync(profiles, therapySettingsRepository, ct);
        return MaximumBasalRateSteps(profiles, versions, to);
    }

    internal static StepTimeline<double> MaximumBasalRateSteps(
        IReadOnlyList<ProfileSpan> profiles, IReadOnlyDictionary<string, TherapySettings[]> versions, long to)
    {
        var steps = new List<Step<double>>();
        foreach (var profile in profiles)
        {
            foreach (var slice in ProfileVersionCache.Slice(
                versions[profile.ProfileName], profile.StartMills, profile.EndMills ?? to))
            {
                if (slice.Version.LoopSettings?.MaximumBasalRatePerHour is { } maximum)
                    steps.Add(new Step<double>(slice.StartMills, slice.EndMills, maximum));
            }
        }
        return StepTimeline<double>.LatestStartedWins(steps);
    }
}
