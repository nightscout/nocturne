using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// IOB and COB at every tick of a window, read from the stored treatments and the AID devices over
/// the bounds <see cref="IobCobSeriesWindow"/> gives.
/// </summary>
/// <remarks>
/// It sits beside <see cref="IobCobReader"/> rather than with <c>IIobCobReader</c> in Core.Contracts
/// because its types are in Core.Clinical, which Core.Contracts cannot reference.
/// </remarks>
public interface IIobCobSeriesReader
{
    /// <summary>
    /// The series over <paramref name="window"/>, with <paramref name="nowMills"/> the present as
    /// <see cref="IobCobSeries.Build"/> reads it. Without <paramref name="readCarbs"/> no carb intake
    /// is read and every tick's calculated COB is none.
    /// </summary>
    Task<IobCobSeriesReading> ReadSeriesAsync(
        IobCobSeriesWindow window, long nowMills, bool readCarbs, CancellationToken ct = default);
}

/// <summary>
/// The ticks of a series and the inputs they were built from, for a caller that also needs those.
/// The temp basals are as read, before <see cref="Nocturne.Core.Clinical.Statistics.StatisticsService.ClipOverlappingTempBasals"/>.
/// </summary>
public sealed record IobCobSeriesReading(
    List<IobCobTick> Ticks,
    List<Bolus> Boluses,
    List<CarbIntake> CarbIntakes,
    List<TempBasal> TempBasals,
    TherapyTimeline Timeline,
    ScheduledBasal Scheduled);
