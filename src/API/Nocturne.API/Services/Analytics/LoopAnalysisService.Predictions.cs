using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

internal sealed partial class LoopAnalysisService
{
    public async Task<PredictionAccuracyResponse> GetPredictionAccuracyAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var (primary, comparison, names) = await ForRangesAsync(
            filter,
            window => LoadPredictionsAsync(window, zone, filter.Masks.ProfileName is not null || filter.ReadProfileNames, filter, ct),
            (loaded, window) => PredictionAccuracyCalculator.Calculate(
                loaded.Inputs, Mask(filter, window, zone, loaded.Sources), window.From, window.To));
        return new PredictionAccuracyResponse { Primary = primary, Comparison = comparison, ProfileNames = names };
    }

    private sealed record PredictionsLoaded(PredictionAccuracyInputs Inputs, LoopAnalysisMaskSources Sources) : ILoaded;

    /// <summary>
    /// Every prediction made in the window; the readings up to
    /// <see cref="PredictionAccuracyCalculator.ReadingsAfterRangeMs"/> past it that the last of them
    /// are paired with, and the spans over the same time, open ones closed at its end; and the
    /// carbs and manual boluses that set the context, from
    /// <see cref="PredictionAccuracyCalculator.ContextLookbackMs"/> before it to
    /// <see cref="PredictionAccuracyCalculator.TreatmentsAfterRangeMs"/> after it. Each cycle counts
    /// once (<see cref="LoopCycles.Distinct"/>), and every APS snapshot is AID evidence for the
    /// open-loop mask, as on the other pages.
    /// </summary>
    private async Task<PredictionsLoaded> LoadPredictionsAsync(
        Window window, TimeZoneInfo zone, bool needsProfiles, LoopAnalysisFilter filter, CancellationToken ct)
    {
        var (from, to) = (window.From, window.To);
        var fromUtc = ToUtc(from);
        var toUtc = ToUtc(to);
        var readingsEnd = to + PredictionAccuracyCalculator.ReadingsAfterRangeMs;
        var readingsEndUtc = ToUtc(readingsEnd);
        var lookbackUtc = ToUtc(from - PredictionAccuracyCalculator.ContextLookbackMs);
        var treatmentsEndUtc = ToUtc(to + PredictionAccuracyCalculator.TreatmentsAfterRangeMs);

        var predictionTask = apsSnapshotRepository.GetPredictionsAsync(fromUtc, toUtc, ct);
        var evidenceTask = apsSnapshotRepository.GetIobCobPointsAsync(ToUtc(from - DeviceReportedValues.RecencyThresholdMs), toUtc, ct);
        var glucoseTask = glucoseRepository.GetForAnalyticsAsync(fromUtc, readingsEndUtc, null, null, int.MaxValue, descending: false, ct: ct);
        var bolusTask = bolusRepository.GetAsync(lookbackUtc, treatmentsEndUtc, null, null, int.MaxValue, descending: false, ct: ct);
        var carbTask = carbIntakeRepository.GetAsync(lookbackUtc, treatmentsEndUtc, null, null, int.MaxValue, descending: false, ct: ct);
        var spanTask = stateSpanRepository.GetByCategories(MaskCategories, fromUtc, readingsEndUtc, ct);
        var deviceTask = patientDeviceRepository.GetByDateRangeAsync(fromUtc, toUtc, ct);
        await Task.WhenAll(predictionTask, evidenceTask, glucoseTask, bolusTask, carbTask, spanTask, deviceTask);

        var snapshots = LoopCycles.Distinct(await predictionTask, p => p.PredictedStart.ToMills(), p => p.Enacted)
            .Select(PredictionAccuracyCalculator.Parse)
            .OfType<PredictionSnapshot>()
            .ToList();
        var glucose = await canonicalGlucose.SelectAsync([.. await glucoseTask], ct);
        var spans = await spanTask;
        var profiles = needsProfiles
            ? ProfileIntervals(await activeProfileResolver.GetProfileSpansForRangeAsync(from, to, ct), to)
            : [];
        var sources = MaskSources(
            spans,
            await deviceTask,
            await deviceDates.ResolveAsync(ct),
            SnapshotMills((await evidenceTask).Select(p => p.Timestamp)),
            profiles,
            readingsEnd);

        var inputs = new PredictionAccuracyInputs
        {
            Zone = zone,
            Snapshots = snapshots,
            Glucose = [.. glucose.Select(g => new GlucoseSample(g.Mills, g.Mgdl)).Where(g => g.Mgdl > 0).OrderBy(g => g.Mills)],
            GlucoseExclusions = IntervalSet.Of(sources.DataExclusions),
            ReadingsUntil = Math.Min(readingsEnd, filter.Now.ToMills()),
            HistoryVisibleFrom = filter.HistoryVisibleFrom is { } edge ? edge.ToMills() : null,
            CarbMills = [.. (await carbTask).Where(c => c.Carbs > 0).Select(c => c.Mills).Order()],
            ManualBolusMills = [.. (await bolusTask).Where(b => b.Kind != BolusKind.Algorithm && b.Insulin > 0).Select(b => b.Mills).Order()],
            Adjusted = IntervalSet.Of(sources.Overrides.Concat(sources.TemporaryTargets).Concat(sources.Exercise)),
        };
        return new PredictionsLoaded(inputs, sources);
    }
}
