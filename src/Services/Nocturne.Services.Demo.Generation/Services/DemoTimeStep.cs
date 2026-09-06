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
    /// <summary>Step time (local, 5-minute cadence).</summary>
    public required DateTime Time { get; init; }

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

    /// <summary>Rate of the temp basal issued at this step, if any.</summary>
    public double? TempBasalRate { get; init; }

    /// <summary>Duration (minutes) of the temp basal issued at this step, if any.</summary>
    public int? TempBasalDuration { get; init; }

    /// <summary>Scenario-effective insulin sensitivity (mg/dL per U) at this step.</summary>
    public required double EffectiveIsf { get; init; }

    /// <summary>Scenario-effective carb ratio (g per U) at this step.</summary>
    public required double EffectiveCarbRatio { get; init; }

    /// <summary>The day's scenario, for downstream generators that shade by day type.</summary>
    public required DayScenario Scenario { get; init; }
}
