using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>
/// The ticks of an <see cref="IobCobSeries"/>, its <c>startTime</c> to its <c>endTime</c>, and every
/// bound its inputs are read over. Every reader of a series takes its bounds from here; a read may
/// reach further for another output it feeds, never less far.
/// </summary>
public sealed record IobCobSeriesWindow(long FirstTick, long LastTick, long IntervalMs)
{
    /// <summary>
    /// Where boluses and carb intakes are read from: <see cref="DailyProfileCalculator.ActingMs"/>
    /// before the first tick, the look-back of insulin and carbs still acting on it.
    /// </summary>
    public long InsulinReadStart => FirstTick - DailyProfileCalculator.ActingMs;

    /// <summary>
    /// Where temp basals are read from: <see cref="StatisticsService.TempBasalReadStart"/> of
    /// <see cref="InsulinReadStart"/>, so a temp that began before the look-back and still ran in
    /// it is read.
    /// </summary>
    public long TempBasalReadStart =>
        StatisticsService.TempBasalReadStart(DateTimeOffset.FromUnixTimeMilliseconds(InsulinReadStart).UtcDateTime).ToMills();

    private readonly long? _readEnd;

    /// <summary>
    /// Where treatments are read to: the last tick, after which none counts, or later for a caller
    /// whose other outputs read the same records further.
    /// </summary>
    public long ReadEnd
    {
        get => _readEnd is { } end ? Math.Max(end, LastTick) : LastTick;
        init => _readEnd = value > LastTick
            ? value
            : value == LastTick
                ? null
                : throw new ArgumentOutOfRangeException(nameof(ReadEnd), value, "Treatments are read at least to the last tick.");
    }

    /// <summary>
    /// Where APS and pump snapshots are read from: <see cref="DeviceReportedValues.RecencyThresholdMs"/>
    /// before the first tick, the oldest snapshot that can still hold at it.
    /// </summary>
    public long DeviceReadStart => FirstTick - DeviceReportedValues.RecencyThresholdMs;

    /// <summary>
    /// Where APS and pump snapshots are read to: <see cref="DeviceReportedValues.FutureSkewToleranceMs"/>
    /// past the last tick, which a live tick admits.
    /// </summary>
    public long DeviceReadEnd => LastTick + DeviceReportedValues.FutureSkewToleranceMs;

    /// <summary>
    /// Where the therapy timeline and the overrides that scale the scheduled basal start: the
    /// look-back, as <see cref="IobCobSeries.Build"/> reads them under every chunk of insulin on board.
    /// </summary>
    public long TherapyStart => InsulinReadStart;

    /// <summary>Where the therapy timeline ends, exclusive: <see cref="IobCobSeries.Build"/> reads it at the last tick.</summary>
    public long TimelineEnd => LastTick + 1;

    /// <summary>Where the overrides that scale the scheduled basal are read to, inclusive.</summary>
    public long OverrideEnd => LastTick;
}
