using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Default <see cref="IErroneousReadingsService"/>: reads the canonical stream with the readings
/// statistics leave out, the sensor-error spans, the notes and the finger sticks, and hands them to
/// <see cref="ErroneousReadingEpisodes"/>, with the patient's zone to cut the days in, the one the
/// page cuts the report's window in.
/// </summary>
public class ErroneousReadingsService(
    ISensorGlucoseRepository sensorGlucoseRepository,
    IStateSpanService stateSpanService,
    INoteRepository noteRepository,
    IMeterGlucoseRepository meterGlucoseRepository,
    IBGCheckRepository bgCheckRepository,
    TenantClockResolver tenantClock) : IErroneousReadingsService
{
    public async Task<ErroneousReadingsReport> GetAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var fromUtc = from.AsUtc();
        var toUtc = to.AsUtc();
        // Finger sticks just outside the window still speak for an episode at its edge.
        var context = TimeSpan.FromMinutes(ErroneousReadingEpisodes.MeterMinutes);

        var glucoseTask = sensorGlucoseRepository.GetCanonicalWithUncountedAsync(
            fromUtc, toUtc, limit: int.MaxValue, descending: false, ct: ct);
        var spansTask = stateSpanService.GetStateSpansAsync(
            StateSpanCategory.DataExclusion, ErroneousReadingEpisodes.SensorErrorState,
            from: fromUtc, to: toUtc, count: int.MaxValue, descending: false, cancellationToken: ct);
        var notesTask = noteRepository.GetAsync(
            fromUtc, toUtc, device: null, source: null, limit: int.MaxValue, descending: false, ct: ct);
        var metersTask = meterGlucoseRepository.GetAsync(
            fromUtc - context, toUtc + context, device: null, source: null, limit: int.MaxValue, descending: false, ct: ct);
        var checksTask = bgCheckRepository.GetAsync(
            fromUtc - context, toUtc + context, device: null, source: null, limit: int.MaxValue, descending: false, ct: ct);

        var clockTask = tenantClock.ResolveAsync(ct);

        await Task.WhenAll(glucoseTask, spansTask, notesTask, metersTask, checksTask, clockTask);

        // An uploader may send one finger stick both ways (xDrip's as meter glucose and a BG check).
        var fingerSticks = (await metersTask)
            .Select(m => new ErroneousEpisodeMeterReading { Mills = m.Mills, Mgdl = m.Mgdl })
            .Concat((await checksTask)
                .Where(c => c.GlucoseType != GlucoseType.Sensor)
                .Select(c => new ErroneousEpisodeMeterReading { Mills = c.Mills, Mgdl = c.Mgdl }))
            .DistinctBy(m => m.Mills);

        return ErroneousReadingEpisodes.Build(
            fromUtc.ToMills(), toUtc.ToMills(), [.. await glucoseTask], await spansTask, await notesTask, fingerSticks,
            (await clockTask).Zone);
    }
}
