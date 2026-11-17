using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// The query parameters every Loop Analysis action takes, bound once as camelCase scalars.
/// </summary>
public sealed class LoopAnalysisQuery
{
    /// <summary>First tenant-local calendar date of the range.</summary>
    [FromQuery(Name = "startDate"), BindRequired]
    public DateOnly StartDate { get; set; }

    /// <summary>
    /// Last tenant-local calendar date of the range, inclusive. A range of more than 90 days keeps
    /// only its most recent 90 (<see cref="LoopAnalysisResolvedRange.Shortened"/>).
    /// </summary>
    [FromQuery(Name = "endDate"), BindRequired]
    public DateOnly EndDate { get; set; }

    /// <summary>Which comparison range to compute, if any.</summary>
    [FromQuery(Name = "compare")]
    public LoopAnalysisComparison Compare { get; set; } = LoopAnalysisComparison.None;

    /// <summary>First local date of a custom comparison range.</summary>
    [FromQuery(Name = "compareStartDate")]
    public DateOnly? CompareStartDate { get; set; }

    /// <summary>Last local date of a custom comparison range, inclusive, under the same 90-day limit.</summary>
    [FromQuery(Name = "compareEndDate")]
    public DateOnly? CompareEndDate { get; set; }

    /// <summary>Local weekdays to analyse; every day when empty.</summary>
    [FromQuery(Name = "days")]
    public DayOfWeek[]? Days { get; set; }

    /// <summary>Mask override time; ignored without treatments.read.</summary>
    [FromQuery(Name = "excludeOverrides")]
    public bool ExcludeOverrides { get; set; }

    /// <summary>Mask temporary target time; ignored without treatments.read.</summary>
    [FromQuery(Name = "excludeTemporaryTargets")]
    public bool ExcludeTemporaryTargets { get; set; }

    /// <summary>Mask exercise time; ignored without treatments.read.</summary>
    [FromQuery(Name = "excludeExercise")]
    public bool ExcludeExercise { get; set; }

    /// <summary>Mask illness time; ignored without treatments.read. On by default.</summary>
    [FromQuery(Name = "excludeIllness")]
    public bool ExcludeIllness { get; set; } = true;

    /// <summary>
    /// Mask time the pump was in manual mode or had no AID algorithm; ignored without devices.read.
    /// On by default.
    /// </summary>
    [FromQuery(Name = "excludeOpenLoop")]
    public bool ExcludeOpenLoop { get; set; } = true;

    /// <summary>Analyse only time this profile was active; ignored without therapy.read.</summary>
    [FromQuery(Name = "profileName")]
    public string? ProfileName { get; set; }

    /// <summary>The masks asked for, with every weekday when none was named.</summary>
    public LoopAnalysisMasks ToMasks() => new()
    {
        Days = Days is { Length: > 0 } ? [.. Days.Distinct().Order()] : [.. Enum.GetValues<DayOfWeek>()],
        ExcludeOverrides = ExcludeOverrides,
        ExcludeTemporaryTargets = ExcludeTemporaryTargets,
        ExcludeExercise = ExcludeExercise,
        ExcludeIllness = ExcludeIllness,
        ExcludeOpenLoop = ExcludeOpenLoop,
        ProfileName = ProfileName,
    };
}
