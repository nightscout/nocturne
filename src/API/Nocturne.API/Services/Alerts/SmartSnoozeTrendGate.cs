using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>Outcome of <see cref="SmartSnoozeTrendGate.Evaluate"/>; only <see cref="Favorable"/> extends.</summary>
internal enum TrendGateOutcome
{
    Favorable,
    NotFavorable,
    InsufficientData,
    NotApplicable,
}

/// <summary>
/// Smart-snooze fallback used when a rule enables smart snooze without configuring conditions:
/// extend an expired snooze only while glucose is clearly moving back toward range.
/// </summary>
/// <remarks>
/// Ported from xDrip's <c>BgReading.trendingToAlertEnd</c>, which compares the newest reading with
/// the one and two readings before it on a 5-minute CGM cadence. Nocturne ingests 1-minute and
/// 5-minute sensors alike, so the lookbacks are fixed wall-clock spans rather than reading counts:
/// on 1-minute data a per-reading delta would demand a 5x steeper climb for a low and accept a
/// 5x shallower fall for a high.
/// <list type="table">
///   <listheader><term>alert</term><description>favorable when (mg/dL, strictly greater)</description></listheader>
///   <item><term>low (<c>below</c>)</term><description>rise over ~5 min &gt; 4, or rise over ~10 min &gt; 10</description></item>
///   <item><term>high (<c>above</c>)</term><description>fall over ~5 min &gt; 1, or fall over ~10 min &gt; 2</description></item>
/// </list>
/// The asymmetry is deliberate and is xDrip's: a high snooze may be held by almost any downward
/// drift, a low one only by a genuine climb, so sensor noise (a few tenths of a mg/dL per minute)
/// never keeps a low silent.
/// <para>
/// Every failure mode is <see cref="TrendGateOutcome.InsufficientData"/> or
/// <see cref="TrendGateOutcome.NotApplicable"/>, both of which clear the snooze so the alert
/// re-fires: a newest reading older than <see cref="MaxLatestAge"/>, or no reading inside either
/// lookback slot (xDrip likewise needs three readings). Only glucose <c>threshold</c> rules have a
/// direction to recover toward; other rule types need explicit snooze conditions.
/// </para>
/// All values are mg/dL, as stored; display units never reach this code.
/// </remarks>
internal static class SmartSnoozeTrendGate
{
    /// <summary>
    /// Two 5-minute CGM intervals plus a minute of upload latency: the newest reading xDrip still
    /// accepts when one packet is missed.
    /// </summary>
    public static readonly TimeSpan MaxLatestAge = TimeSpan.FromMinutes(11);

    public static readonly TimeSpan ShortLookback = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongLookback = TimeSpan.FromMinutes(10);

    /// <summary>Half-width of each lookback slot; the two slots tile 2.5–12.5 minutes before the newest reading.</summary>
    public static readonly TimeSpan SlotTolerance = TimeSpan.FromMinutes(2.5);

    public const double LowMinRiseShort = 4;
    public const double LowMinRiseLong = 10;
    public const double HighMinFallShort = 1;
    public const double HighMinFallLong = 2;

    /// <summary>How far back a caller must fetch readings for <see cref="Evaluate"/> to see both slots.</summary>
    public static TimeSpan RequiredHistory => MaxLatestAge + LongLookback + SlotTolerance;

    /// <param name="conditionType">The rule's root condition type.</param>
    /// <param name="conditionParams">The rule's root condition params, as stored.</param>
    /// <param name="readings">Canonical readings in any order, mg/dL.</param>
    /// <param name="now">Evaluation instant; the newest reading's age is measured from it.</param>
    public static TrendGateOutcome Evaluate(
        AlertConditionType conditionType,
        string conditionParams,
        IReadOnlyList<GlucosePoint> readings,
        DateTime now)
    {
        if (conditionType != AlertConditionType.Threshold)
            return TrendGateOutcome.NotApplicable;

        string? direction;
        try
        {
            direction = JsonSerializer.Deserialize<ThresholdCondition>(conditionParams, EvaluatorJson.Options)
                ?.Direction?.ToLowerInvariant();
        }
        catch (JsonException)
        {
            return TrendGateOutcome.NotApplicable;
        }

        if (direction is not ("below" or "above"))
            return TrendGateOutcome.NotApplicable;

        if (readings.Count == 0)
            return TrendGateOutcome.InsufficientData;

        var latest = readings.MaxBy(r => r.Timestamp);
        if (now - latest.Timestamp > MaxLatestAge)
            return TrendGateOutcome.InsufficientData;

        var shortRef = ClosestTo(readings, latest.Timestamp - ShortLookback);
        var longRef = ClosestTo(readings, latest.Timestamp - LongLookback);
        if (shortRef is null || longRef is null)
            return TrendGateOutcome.InsufficientData;

        var changeShort = latest.Mgdl - shortRef.Value.Mgdl;
        var changeLong = latest.Mgdl - longRef.Value.Mgdl;

        var favorable = direction == "below"
            ? changeShort > LowMinRiseShort || changeLong > LowMinRiseLong
            : -changeShort > HighMinFallShort || -changeLong > HighMinFallLong;

        return favorable ? TrendGateOutcome.Favorable : TrendGateOutcome.NotFavorable;
    }

    private static GlucosePoint? ClosestTo(IReadOnlyList<GlucosePoint> readings, DateTime target)
        => readings
            .Where(r => (r.Timestamp - target).Duration() <= SlotTolerance)
            .Select(r => (GlucosePoint?)r)
            .MinBy(r => (r!.Value.Timestamp - target).Duration());
}

/// <summary>A glucose reading reduced to what <see cref="SmartSnoozeTrendGate"/> reads.</summary>
internal readonly record struct GlucosePoint(DateTime Timestamp, double Mgdl);
