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

        // Get mean values (use Mgdl if available, otherwise Sgv)
        var recentMean = recent.Mgdl != 0 ? recent.Mgdl : recent.Sgv ?? 0;
        var previousMean = previous.Mgdl != 0 ? previous.Mgdl : previous.Sgv ?? 0;

        if (recentMean == 0 || previousMean == 0)
            return null;

        // Calculate absolute difference and elapsed time
        var absolute = recentMean - previousMean;
        var elapsedMins = (recent.Mills - previous.Mills) / (60.0 * 1000); // Legacy interpolation logic - exactly matching bgnow.js
        var isInterpolated = elapsedMins > 9; // Legacy uses 9 minutes threshold        // Calculate mean5MinsAgo - exact legacy algorithm
        var mean5MinsAgo = isInterpolated
            ? recentMean - ((recentMean - previousMean) / elapsedMins) * 5
            : recentMean - (recentMean - previousMean);

        // Calculate mg/dL delta - exact legacy rounding
        var mgdl = (int)Math.Round(recentMean - mean5MinsAgo);

        // Scale for display - legacy scaling logic
        var scaled =
            units == "mmol"
                ? RoundBGToDisplayFormat(ScaleMgdl(recentMean) - ScaleMgdl(mean5MinsAgo))
                : mgdl; // Format display - exact legacy formatting
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
            Previous = previous,
            Current = recent,
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

    /// <summary>
    /// Scale mg/dL to mmol/L - exact legacy conversion
    /// </summary>
    private static double ScaleMgdl(double mgdl)
    {
        return mgdl / 18.0;
    }

    /// <summary>
    /// Round BG to display format - exact legacy rounding
    /// </summary>
    private static double RoundBGToDisplayFormat(double value)
    {
        return Math.Round(value, 1);
    }
}
