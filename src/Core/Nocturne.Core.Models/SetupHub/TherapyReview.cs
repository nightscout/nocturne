using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.SetupHub;

/// <summary>Where the tenant's active therapy profile came from, as the setup hub reviews it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TherapySource>))]
public enum TherapySource
{
    /// <summary>No therapy profile yet.</summary>
    None,

    /// <summary>
    /// Sent by an automated insulin delivery system, uploader or connector, which owns it: a
    /// change made in Nocturne is overwritten by its next sync.
    /// </summary>
    Synced,

    /// <summary>Written by a Nightscout import and not replaced since.</summary>
    Imported,

    /// <summary>Entered by hand in the setup hub.</summary>
    Entered,
}

/// <summary>A glucose-dependent therapy value whose unit the setup hub checks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TherapyGlucoseField>))]
public enum TherapyGlucoseField
{
    Sensitivity,
    Target,
}

/// <summary>
/// A value of <paramref name="Field"/> entered in <paramref name="GlucoseUnits"/> that lies below
/// <paramref name="Below"/> or above <paramref name="Above"/> reads as a number in the other glucose
/// unit. It flags a likely unit mix-up; it says nothing about whether a value suits anyone.
/// </summary>
/// <param name="GlucoseUnits">"mg/dl" or "mmol", as the owner's display units are stored.</param>
public record WrongUnitRule(TherapyGlucoseField Field, string GlucoseUnits, double? Below, double? Above)
{
    public bool Flags(double value) => value < Below || value > Above;
}

public static class TherapyUnitPlausibility
{
    /// <summary>
    /// ISF runs roughly 10-400 mg/dL per unit (0.5-22 mmol/L per unit), and targets 70-180 mg/dL
    /// (4-10 mmol/L), so each bound sits in the gap between one unit's range and the other's.
    /// </summary>
    public static readonly IReadOnlyList<WrongUnitRule> Rules =
    [
        new(TherapyGlucoseField.Sensitivity, "mg/dl", Below: 8, Above: null),
        new(TherapyGlucoseField.Sensitivity, "mmol", Below: null, Above: 25),
        new(TherapyGlucoseField.Target, "mg/dl", Below: 30, Above: null),
        new(TherapyGlucoseField.Target, "mmol", Below: null, Above: 25),
    ];

    public static bool LooksLikeTheOtherUnit(TherapyGlucoseField field, string glucoseUnits, double value) =>
        Rules.Any(r => r.Field == field && r.GlucoseUnits == glucoseUnits && r.Flags(value));
}

/// <summary>
/// The tenant's active therapy profile as the setup hub reviews it. Glucose values in the schedules
/// are mg/dL, as every V4 therapy schedule stores them.
/// </summary>
/// <param name="SourceName">The app that sent a <see cref="TherapySource.Synced"/> profile, when it says.</param>
/// <param name="LastUpdated">When the profile's newest part was made at its source.</param>
/// <param name="Confirmed">The owner said a synced or imported profile matches their app.</param>
/// <param name="WrongUnitRules">The checks the manual entry form flags values against.</param>
public record TherapyReview(
    TherapySource Source,
    string? SourceName,
    DateTime? LastUpdated,
    bool Confirmed,
    TherapySettings? Settings,
    BasalSchedule? Basal,
    CarbRatioSchedule? CarbRatio,
    SensitivitySchedule? Sensitivity,
    TargetRangeSchedule? TargetRange,
    IReadOnlyList<WrongUnitRule> WrongUnitRules);

/// <summary>One time block of a hand-entered schedule; a null value is a field left blank.</summary>
public record TherapyEntryInput(string Time, double? Value);

/// <summary>One time block of a hand-entered target range, in the units the form was filled in.</summary>
public record TargetEntryInput(string Time, double? Low, double? High);
