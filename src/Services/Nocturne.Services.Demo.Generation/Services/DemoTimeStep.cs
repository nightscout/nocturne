using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// One 5-minute step of the demo simulation. Every generated stream — CGM
/// entries, treatments, device status, alarm episodes — derives from the same
/// timeline, so IOB/COB in device status match the boluses on the chart and
/// the fingersticks land near the CGM trace they were generated from.
/// </summary>
public sealed record DemoTimeStep
{
    /// <summary>
    /// The step's wall-clock time in the generator's zone, for the daily pattern. It repeats across a
    /// fall-back hour; <see cref="Utc"/> does not.
    /// </summary>
    public required DateTime Time { get; init; }

    /// <summary>The step's instant, on a 5-minute cadence.</summary>
    public required DateTime Utc { get; init; }

    /// <summary>The CGM entry for this step.</summary>
    public required Entry Entry { get; init; }

    /// <summary>
    /// Additional non-sgv entries at this step: fingerstick <c>mbg</c> entries
    /// and sensor-calibration <c>cal</c> entries.
    /// </summary>
    public IReadOnlyList<Entry> ExtraEntries { get; init; } = [];

    /// <summary>Treatments issued at this step (boluses, carbs, temp basals, notes).</summary>
    public IReadOnlyList<Treatment> Treatments { get; init; } = [];

    /// <summary>Insulin on board from the oref simulator, after this step's doses.</summary>
    public required double Iob { get; init; }

    /// <summary>Carbs on board from the oref simulator, after this step's doses.</summary>
    public required double Cob { get; init; }

    /// <summary>
    /// Rate of the temp basal the loop chose at this step, if any. The pump ran it only when
    /// <see cref="Loop"/> is <see cref="DemoLoopState.Closed"/>; in manual mode it is a suggestion.
    /// </summary>
    public double? TempBasalRate { get; init; }

    /// <summary>Duration (minutes) of <see cref="TempBasalRate"/>.</summary>
    public int? TempBasalDuration { get; init; }

    /// <summary>The rate the loop asked for before its maxSafeBasal cut it to <see cref="TempBasalRate"/>.</summary>
    public double? RequestedBasalRate { get; init; }

    /// <summary>The loop's maxSafeBasal (U/h) at this step.</summary>
    public double? MaxSafeBasalRate { get; init; }

    /// <summary>What the loop was doing at this step.</summary>
    public DemoLoopState Loop { get; init; }

    /// <summary>Scenario-effective insulin sensitivity (mg/dL per U) at this step.</summary>
    public required double EffectiveIsf { get; init; }

    /// <summary>Scenario-effective carb ratio (g per U) at this step.</summary>
    public required double EffectiveCarbRatio { get; init; }

    /// <summary>The day's scenario, for downstream generators that shade by day type.</summary>
    public required DayScenario Scenario { get; init; }
}
