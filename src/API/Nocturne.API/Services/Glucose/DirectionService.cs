using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Static direction helpers that implement the exact algorithms from the legacy Nightscout JavaScript
/// files <c>direction.js</c> and <c>bgnow.js</c>, preserving 1:1 API compatibility.
/// All methods are pure functions with no side effects or dependencies.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CalculateDelta"/> mirrors the legacy interpolation rule: when two consecutive
/// readings are more than 9 minutes apart (<c>isInterpolated = true</c>) the 5-minute delta
/// is computed via linear interpolation between the two readings rather than taking the raw
/// difference. This prevents anomalously large deltas when readings arrive late.
/// </para>
/// <para>
/// <see cref="CalculateDirection(double, double, double)"/> maps slope (mg/dL per minute) to
/// <see cref="Direction"/> using the original Nightscout thresholds:
/// &gt;3 TripleUp, &gt;2 DoubleUp, ≥1 SingleUp, &gt;0 FortyFiveUp, &gt;-0.5 Flat,
/// &gt;-1 FortyFiveDown, &gt;-2 SingleDown, ≥-3 DoubleDown, else TripleDown.
/// </para>
/// <para>
/// mmol/L display formatting rounds to one decimal place with explicit sign, matching legacy
/// behaviour. mg/dL values are rounded to the nearest integer.
/// </para>
/// </remarks>
public static class DirectionService
{
    private const int BucketCount = 4;
    private const long BucketWidthMills = 5 * 60 * 1000;
    private const long BucketHalfWidthMills = BucketWidthMills / 2;
    private const double ErrorCodeCeiling = 39;

    /// <summary>
    /// Direction to character mapping - exact legacy mapping from direction.js
    /// </summary>
    private static readonly Dictionary<Direction, string> DirectionCharMap = new()
    {
        { Direction.NONE, "⇼" },
        { Direction.TripleUp, "⤊" },
        { Direction.DoubleUp, "⇈" },
        { Direction.SingleUp, "↑" },
        { Direction.FortyFiveUp, "↗" },
        { Direction.Flat, "→" },
        { Direction.FortyFiveDown, "↘" },
        { Direction.SingleDown, "↓" },
        { Direction.DoubleDown, "⇊" },
        { Direction.TripleDown, "⤋" },
        { Direction.NotComputable, "-" },
        { Direction.RateOutOfRange, "⇕" },
    };

    /// <summary>
    /// Get direction information for display - exact legacy algorithm
    /// </summary>
    public static Core.Models.DirectionInfo GetDirectionInfo(Entry? entry)
    {
        var result = new Core.Models.DirectionInfo { Display = null };

        if (entry == null)
            return result;

        var direction = DirectionExtensions.Parse(entry.Direction);
        result.Value = direction;
        result.Label = DirectionToChar(direction);
        result.Entity = CharToEntity(result.Label);

        return result;
    }

    /// <summary>
    /// Calculate glucose delta between current and previous readings - exact legacy algorithm
    /// </summary>
    public static Core.Models.DeltaInfo? CalculateDelta(IList<Entry> entries, string units)
    {
        if (entries.Count < 2)
            return null;

        var sortedEntries = entries.OrderByDescending(e => e.Mills).ToList();
        var recent = sortedEntries[0];
        var previous = sortedEntries[1];

        return BuildDelta(recent.GlucoseMgdl(), previous.GlucoseMgdl(), recent, previous, units);
    }

    /// <summary>
    /// Builds the 5 minute buckets <c>bgnow.js</c> reads, newest first. The newest reading anchors
    /// bucket 0 at 2.5 minutes either side of it, and each older bucket sits 5 minutes earlier.
    /// </summary>
    public static IReadOnlyList<BgNowBucket> BuildBuckets(IEnumerable<Entry> entries)
    {
        var sorted = entries.OrderBy(e => e.Mills).ToList();
        if (sorted.Count == 0)
            return [];

        var last = sorted[^1].Mills;
        var buckets = new List<BgNowBucket>(BucketCount);
        for (var i = 0; i < BucketCount; i++)
        {
            var fromMills = last - BucketHalfWidthMills - i * BucketWidthMills;
            var toMills = fromMills + BucketWidthMills;
            var inBucket = sorted.Where(e => e.Mills >= fromMills && e.Mills < toMills).ToList();
            buckets.Add(
                new BgNowBucket(
                    fromMills,
                    inBucket,
                    inBucket.Where(e => e.GlucoseMgdl() > ErrorCodeCeiling).ToList(),
                    inBucket.Where(e => e.GlucoseMgdl() < ErrorCodeCeiling).ToList()
                )
            );
        }

        return buckets;
    }

    /// <summary>
    /// The change from the newest bucket (always holding the newest reading) to the next bucket holding a valid reading, taken on their
    /// means. Null when the newest bucket holds only error codes or no older bucket has a valid reading.
    /// </summary>
    public static BucketDelta? CalculateBucketDelta(
        IReadOnlyList<BgNowBucket> buckets,
        string units
    )
    {
        var recent = buckets.FirstOrDefault();
        if (recent?.Last == null)
            return null;

        var previous = buckets.Skip(1).FirstOrDefault(b => b.Last != null);
        if (previous == null)
            return null;

        var info = BuildDelta(
            recent.Mean!.Value,
            previous.Mean!.Value,
            recent.Last,
            previous.Last,
            units
        );
        return info == null ? null : new BucketDelta(info, previous);
    }

    private static Core.Models.DeltaInfo? BuildDelta(
        double recentMean,
        double previousMean,
        Entry? recent,
        Entry? previous,
        string units
    )
    {
        if (recent == null || previous == null || recentMean == 0 || previousMean == 0)
            return null;

        var absolute = recentMean - previousMean;
        var elapsedMins = (recent.Mills - previous.Mills) / (60.0 * 1000);
        var isInterpolated = elapsedMins > 9;
        var mean5MinsAgo = isInterpolated
            ? recentMean - ((recentMean - previousMean) / elapsedMins) * 5
            : previousMean;

        var mgdl = (int)Math.Floor(recentMean - mean5MinsAgo + 0.5);

        var scaled =
            units == "mmol"
                ? RoundBGToDisplayFormat(ScaleMgdl(recentMean) - ScaleMgdl(mean5MinsAgo))
                : mgdl;
        var display =
            units == "mmol"
                ? (scaled >= 0 ? $"+{scaled:F1}" : $"{scaled:F1}")
                : (scaled >= 0 ? $"+{scaled}" : scaled.ToString());
        return new Core.Models.DeltaInfo
        {
            Absolute = absolute,
            ElapsedMins = elapsedMins,
            Interpolated = isInterpolated,
            Mean5MinsAgo = mean5MinsAgo,
            Mgdl = mgdl,
            Scaled = scaled,
            Display = display,
            Times = new Dictionary<string, long>
            {
                ["recent"] = recent.Mills,
                ["previous"] = previous.Mills,
            },
        };
    }

    /// <summary>
    /// Calculate direction from slope and delta values
    /// </summary>
    public static Direction CalculateDirection(double current, double previous, double deltaMinutes)
    {
        if (deltaMinutes <= 0)
            return Direction.NONE;

        var mgdlDelta = current - previous;
        var slope = mgdlDelta / deltaMinutes; // mg/dL per minute        // Apply direction thresholds - based on legacy Nightscout thresholds
        return slope switch
        {
            > 3 => Direction.TripleUp,
            > 2 => Direction.DoubleUp,
            >= 1 => Direction.SingleUp,
            > 0 => Direction.FortyFiveUp,
            > -0.5 => Direction.Flat,
            > -1 => Direction.FortyFiveDown,
            > -2 => Direction.SingleDown,
            >= -3 => Direction.DoubleDown,
            _ => Direction.TripleDown,
        };
    }

    /// <summary>
    /// Get direction character mapping - exact legacy mapping
    /// </summary>
    public static string DirectionToChar(Direction direction)
    {
        return DirectionCharMap.TryGetValue(direction, out var character) ? character : "-";
    }

    /// <summary>
    /// Convert character to HTML entity - exact legacy method
    /// </summary>
    public static string CharToEntity(string character)
    {
        if (string.IsNullOrEmpty(character) || character.Length == 0)
            return string.Empty;

        return $"&#{(int)character[0]};";
    }

    private static double ScaleMgdl(double mgdl)
    {
        return mgdl / GlucoseConstants.MgdlPerMmol;
    }

    /// <summary>
    /// Round BG to display format - exact legacy rounding
    /// </summary>
    private static double RoundBGToDisplayFormat(double value)
    {
        return Math.Round(value, 1);
    }
}

/// <summary>
/// One 5 minute window of readings. <see cref="Mean"/>, <see cref="Last"/> and
/// <see cref="Entry.Mills"/> of <see cref="Last"/> come from the readings above 39 mg/dL; readings
/// below 39 are sensor error codes and land in <see cref="Errors"/>, and 39 itself is in neither.
/// </summary>
public sealed record BgNowBucket(
    long FromMills,
    IReadOnlyList<Entry> Entries,
    IReadOnlyList<Entry> Valid,
    IReadOnlyList<Entry> Errors
)
{
    public Entry? Last => Valid.Count > 0 ? Valid[^1] : null;

    public double? Mean => Valid.Count > 0 ? Valid.Average(e => e.GlucoseMgdl()) : null;
}

/// <summary>
/// A bucket delta with the older bucket it was taken against, which <c>bgnow.js</c> publishes as
/// <c>delta.previous</c>.
/// </summary>
public sealed record BucketDelta(Core.Models.DeltaInfo Info, BgNowBucket Previous);
