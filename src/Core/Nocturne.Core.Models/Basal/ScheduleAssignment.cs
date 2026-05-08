using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Basal;

/// <summary>
/// One profile's assignment over a half-open time interval, fully resolved with its
/// schedule entries, CCP adjustment, and timezone.
/// </summary>
/// <remarks>
/// The orchestrator builds these by combining a profile <see cref="StateSpan"/> (which provides
/// the active profile name + CCP metadata) with the matching <see cref="BasalSchedule"/> and
/// the timezone string from <c>therapy_settings</c>. The pure <c>BasalCalculator</c> consumes a
/// list of these covering the query window and emits per-rate segments.
/// </remarks>
/// <param name="StartMills">Inclusive start of the assignment, Unix ms.</param>
/// <param name="EndMills">Exclusive end of the assignment, Unix ms (or <c>long.MaxValue</c> for open-ended).</param>
/// <param name="ProfileName">Profile name in effect.</param>
/// <param name="Entries">Schedule entries (HH:mm + U/hr) for this profile, sorted by <c>TimeAsSeconds</c> ascending.</param>
/// <param name="Percentage">CCP percentage scaling (100 = unchanged).</param>
/// <param name="TimeshiftMs">CCP timeshift applied to the schedule lookup, in ms (0 = unshifted).</param>
/// <param name="TimeZoneId">IANA or Windows timezone ID for resolving local time-of-day boundaries.</param>
public readonly record struct ScheduleAssignment(
    long StartMills,
    long EndMills,
    string ProfileName,
    IReadOnlyList<ScheduleEntry> Entries,
    double Percentage,
    long TimeshiftMs,
    string? TimeZoneId);
