using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The glucose trend the alert engine judges a reading by: the <c>rate_of_change</c> rate and the
/// <c>trend</c> bucket of <see cref="SensorContext"/>.
/// </summary>
/// <remarks>
/// A stored (vendor) <see cref="SensorGlucose.TrendRate"/> is used unchanged. Without one, the rate
/// is derived from the reading and the nearest earlier reading of its stream
/// (<see cref="CanonicalGlucoseStream.StreamKey"/>) from its uploader
/// (<see cref="SensorGlucose.DataSource"/>), when that reading is at least <see cref="MinGap"/>
/// and at most <see cref="MaxGap"/> older; many uploaders send only the arrow.
/// <para>
/// The uploader must match because two apps uploading one registered CGM give one stream, and
/// their copies of one reading can sit too far apart for deduplication to link them: the other
/// app's copy would then pair with the reading as if it were the reading before. A null and an
/// empty data source are one uploader, as in the stream key. The earlier reading may be a copy
/// deduplication hides or a reading of a bucket its stream lost, since the uploader's own previous
/// reading is often the hidden one. A copy in the reading's own duplicate group is still left
/// out: one uploader can send a reading twice under two ids at one timestamp, and that copy is the
/// same reading, not the one before.
/// </para>
/// With no rate, the bucket falls back to the stored <see cref="SensorGlucose.Direction"/>; the
/// rate stays null, so <c>rate_of_change</c> stays numeric.
/// </remarks>
internal readonly record struct ReadingTrend(decimal? Rate, TrendBucket? Bucket)
{
    /// <summary>
    /// The narrowest gap a derived rate spans. Under it, value jitter dominates the quotient, and
    /// a pseudo-device holding more than one sensor pairs readings of two of them.
    /// </summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    /// <summary>The widest gap a derived rate spans. Over it, the rate is not the reading's trend.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The trend of <paramref name="reading"/>. <paramref name="earlier"/> holds candidate readings
    /// of any stream in any order, each with the primary of its duplicate group when it is a
    /// non-primary copy; only the nearest earlier one of the reading's stream is used.
    /// </summary>
    public static ReadingTrend Of(
        SensorGlucose reading, IEnumerable<(SensorGlucose Reading, Guid? DuplicateOf)> earlier)
    {
        var rate = reading.TrendRate?.ToDecimal() ?? Derive(reading, PreviousOf(reading, earlier));
        return new ReadingTrend(rate, rate is { } r ? BucketOf(r) : BucketOf(reading.Direction));
    }

    /// <summary>
    /// <see cref="Of"/> over the one reading <see cref="ICanonicalGlucoseService.GetStreamPredecessorAsync"/>
    /// returns for the <see cref="MaxGap"/> before <paramref name="reading"/>, read only when a rate
    /// can be derived.
    /// </summary>
    public static async Task<ReadingTrend> ForAsync(
        SensorGlucose reading, ICanonicalGlucoseService canonical, CancellationToken ct)
    {
        if (reading.TrendRate is not null || reading.Mgdl <= 0)
            return Of(reading, []);

        var previous = await canonical.GetStreamPredecessorAsync(reading, reading.Timestamp - MaxGap, ct);
        return Of(reading, previous is null ? [] : [(previous, null)]);
    }

    /// <summary>
    /// The newest reading of <paramref name="reading"/>'s stream and uploader with a glucose value,
    /// timestamped at or before it and within <see cref="MaxGap"/>, leaving out the reading's own
    /// duplicate copies. One under <see cref="MinGap"/> older is returned, so it yields no rate rather than a
    /// rate against an older reading.
    /// </summary>
    private static SensorGlucose? PreviousOf(
        SensorGlucose reading, IEnumerable<(SensorGlucose Reading, Guid? DuplicateOf)> candidates)
    {
        var stream = CanonicalGlucoseStream.StreamKey(reading);
        var source = reading.DataSource ?? "";
        return candidates
            .Where(c => c.DuplicateOf != reading.Id)
            .Select(c => c.Reading)
            .Where(c => c.Id != reading.Id
                        && c.Mgdl > 0
                        && c.Timestamp <= reading.Timestamp
                        && reading.Timestamp - c.Timestamp <= MaxGap
                        && (c.DataSource ?? "") == source
                        && CanonicalGlucoseStream.StreamKey(c) == stream)
            .MaxBy(c => c.Timestamp);
    }

    private static decimal? Derive(SensorGlucose reading, SensorGlucose? previous)
    {
        if (previous is null || reading.Mgdl <= 0) return null;

        var gap = reading.Timestamp - previous.Timestamp;
        if (gap < MinGap) return null;

        decimal minutes = gap.Ticks;
        minutes /= TimeSpan.TicksPerMinute;
        return (reading.Mgdl.ToDecimal() - previous.Mgdl.ToDecimal()) / minutes;
    }

    /// <summary>
    /// Boundary rates fall into the more aggressive bucket, matching the "rising" CGM clients show
    /// at +1 mg/dL/min.
    /// </summary>
    internal static TrendBucket BucketOf(decimal rate) => rate switch
    {
        >= 3.0m => TrendBucket.RisingFast,
        >= 1.0m => TrendBucket.Rising,
        >= -1.0m => TrendBucket.Flat,
        >= -3.0m => TrendBucket.Falling,
        _ => TrendBucket.FallingFast,
    };

    /// <summary>
    /// <see cref="BucketOf(decimal)"/> read through the rate range each arrow stands for
    /// (<see cref="GlucoseDirection"/>).
    /// </summary>
    internal static TrendBucket? BucketOf(GlucoseDirection? direction) => direction switch
    {
        GlucoseDirection.DoubleUp => TrendBucket.RisingFast,
        GlucoseDirection.SingleUp or GlucoseDirection.FortyFiveUp => TrendBucket.Rising,
        GlucoseDirection.Flat => TrendBucket.Flat,
        GlucoseDirection.FortyFiveDown or GlucoseDirection.SingleDown => TrendBucket.Falling,
        GlucoseDirection.DoubleDown => TrendBucket.FallingFast,
        _ => null,
    };
}
