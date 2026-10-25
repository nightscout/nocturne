namespace Nocturne.Core.Models.V4;

/// <summary>
/// One profile read over part of a window, with the schedules in force while it was read.
/// </summary>
/// <remarks>
/// Each schedule is the version read for the longest time while the profile was in force; the matching
/// change info says when another version was read for part of it. Sensitivity and targets are mg/dL.
/// </remarks>
public class ActiveProfileSchedules
{
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Milliseconds of the window the profile was read over.</summary>
    public long ActiveMs { get; set; }

    /// <summary>The fraction of the window, 0 to 1, the profile was read over.</summary>
    public double Share { get; set; }

    /// <summary>
    /// The percentage the activation in force longest applies to the schedules, or null when none
    /// changes them. In the entries here only when <see cref="AdjustmentApplied"/>.
    /// </summary>
    public double? Percentage { get; set; }

    /// <summary>
    /// The timeshift in hours the activation in force longest applies to the schedules, or null when
    /// none changes them. In the entries here only when <see cref="AdjustmentApplied"/>.
    /// </summary>
    public double? TimeshiftHours { get; set; }

    /// <summary>
    /// Whether <see cref="Percentage"/> and <see cref="TimeshiftHours"/> are already in the entries, as
    /// they are in a schedule the uploader sent with them applied.
    /// </summary>
    public bool AdjustmentApplied { get; set; }

    public List<ScheduleEntry> Basal { get; set; } = [];

    public List<ScheduleEntry> CarbRatio { get; set; } = [];

    public List<ScheduleEntry> Sensitivity { get; set; } = [];

    public List<TargetRangeEntry> Targets { get; set; } = [];

    public ScheduleChangeInfo BasalChanges { get; set; } = new();

    public ScheduleChangeInfo CarbRatioChanges { get; set; } = new();

    public ScheduleChangeInfo SensitivityChanges { get; set; } = new();

    public ScheduleChangeInfo TargetRangeChanges { get; set; } = new();
}
