using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>A schedule a profile carries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileAttribute>))]
public enum ProfileAttribute
{
    /// <summary>U/h.</summary>
    Basal,

    /// <summary>mg/dL per U.</summary>
    Sensitivity,

    /// <summary>g per U.</summary>
    CarbRatio,

    /// <summary>mg/dL, a low and a high.</summary>
    TargetRange,
}

/// <summary>
/// One entry of a schedule as it was in force: the activation's percentage and timeshift are in it.
/// </summary>
public class ProfileHistoryEntry
{
    /// <summary>Seconds from local midnight the entry starts at.</summary>
    public int TimeAsSeconds { get; set; }

    /// <summary>The value, or the low of a target range.</summary>
    public double Value { get; set; }

    /// <summary>The high of a target range; null for every other schedule.</summary>
    public double? High { get; set; }
}

/// <summary>
/// A stretch of time over which the profile read, its percentage and timeshift, and all four of its
/// schedules stayed the same.
/// </summary>
public class ProfileVersion
{
    public long StartMills { get; set; }

    public long EndMills { get; set; }

    public string ProfileName { get; set; } = string.Empty;

    /// <summary>The activation's percentage, or null when it leaves the schedules as stored.</summary>
    public double? Percentage { get; set; }

    /// <summary>The activation's timeshift in hours, or null when it leaves the schedules as stored.</summary>
    public double? TimeshiftHours { get; set; }

    /// <summary>The IANA zone the entries' times of day are read in, or null for UTC.</summary>
    public string? Timezone { get; set; }

    public List<ProfileHistoryEntry> Basal { get; set; } = [];

    public List<ProfileHistoryEntry> Sensitivity { get; set; } = [];

    public List<ProfileHistoryEntry> CarbRatio { get; set; } = [];

    public List<ProfileHistoryEntry> Targets { get; set; } = [];
}

/// <summary>
/// One time of day of a schedule on both sides of a <see cref="ProfileChange"/>. A side is null when
/// that side had no schedule.
/// </summary>
public class ProfileChangeRow
{
    public int TimeAsSeconds { get; set; }

    public double? Before { get; set; }

    public double? After { get; set; }

    /// <summary>The high of the target range before; null for every other schedule.</summary>
    public double? BeforeHigh { get; set; }

    /// <summary>The high of the target range after; null for every other schedule.</summary>
    public double? AfterHigh { get; set; }

    public bool Changed { get; set; }
}

/// <summary>A schedule that differs across a <see cref="ProfileChange"/>.</summary>
public class ProfileAttributeChange
{
    public ProfileAttribute Attribute { get; set; }

    /// <summary>One row per time of day either side has an entry at, in order.</summary>
    public List<ProfileChangeRow> Rows { get; set; } = [];
}

/// <summary>The boundary between two <see cref="ProfileVersion"/>s.</summary>
public class ProfileChange
{
    public long Mills { get; set; }

    public string PreviousProfileName { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public double? PreviousPercentage { get; set; }

    public double? Percentage { get; set; }

    public double? PreviousTimeshiftHours { get; set; }

    public double? TimeshiftHours { get; set; }

    /// <summary>
    /// The activation that starts at this boundary, which makes the change a profile switch; null when
    /// a stored schedule was edited or an activation ended.
    /// </summary>
    public ProfileActivationDto? Activation { get; set; }

    /// <summary>The schedules that differ, in the order of <see cref="ProfileAttribute"/>. Empty when only the profile's name does.</summary>
    public List<ProfileAttributeChange> Attributes { get; set; } = [];
}

/// <summary>The profile versions of a window, oldest first without overlap or gap, and the changes between them.</summary>
public class ProfileHistory
{
    public List<ProfileVersion> Versions { get; set; } = [];

    public List<ProfileChange> Changes { get; set; } = [];
}

/// <summary>A stretch of real time over which a scheduled value holds.</summary>
public class ProfileLaneStep
{
    public long StartMills { get; set; }

    public long EndMills { get; set; }

    /// <summary>The value, or the low of a target range.</summary>
    public double Value { get; set; }

    /// <summary>The high of a target range; null for every other lane.</summary>
    public double? High { get; set; }
}

/// <summary>What a loop cycle reported the sensitivity to be.</summary>
public class ProfileLoopPoint
{
    public long Mills { get; set; }

    /// <summary>The cycle's autosens or dynamic ISF ratio.</summary>
    public double SensitivityRatio { get; set; }

    /// <summary>The scheduled sensitivity at the cycle divided by <see cref="SensitivityRatio"/>, in mg/dL per U.</summary>
    public double Sensitivity { get; set; }
}

/// <summary>
/// The scheduled value of each <see cref="ProfileAttribute"/> over a window in real time, and what
/// moved it: loop cycles for sensitivity, temporary targets for the target range.
/// </summary>
public class ProfileLanes
{
    public List<ProfileLaneStep> Basal { get; set; } = [];

    public List<ProfileLaneStep> Sensitivity { get; set; } = [];

    public List<ProfileLaneStep> CarbRatio { get; set; } = [];

    public List<ProfileLaneStep> Targets { get; set; } = [];

    /// <summary>One point per loop cycle that reported a ratio, oldest first; null when the caller may not read device data.</summary>
    public List<ProfileLoopPoint>? Loop { get; set; }

    /// <summary>The temporary targets that ran in the window, in mg/dL, oldest first; null when the caller may not read treatments.</summary>
    public List<ProfileLaneStep>? TemporaryTargets { get; set; }
}

/// <summary>The sensor glucose readings of one bucket of time, in mg/dL.</summary>
public class GlucoseTracePoint
{
    /// <summary>The middle of the bucket.</summary>
    public long Mills { get; set; }

    public double Average { get; set; }

    public double Min { get; set; }

    public double Max { get; set; }
}

/// <summary>The sensor glucose of a window.</summary>
public class GlucoseWindowSummary
{
    public long StartMills { get; set; }

    public long EndMills { get; set; }

    public int Readings { get; set; }

    /// <summary>mg/dL; null without readings.</summary>
    public double? Average { get; set; }

    /// <summary>Percent of readings in the consensus target range; null without readings.</summary>
    public double? TimeInRange { get; set; }
}

/// <summary>The sensor glucose of two windows of equal length, one ending and one starting at an instant.</summary>
public class GlucoseAroundChange
{
    public GlucoseWindowSummary Before { get; set; } = new();

    public GlucoseWindowSummary After { get; set; } = new();
}
