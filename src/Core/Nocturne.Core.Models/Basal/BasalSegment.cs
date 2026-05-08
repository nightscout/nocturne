namespace Nocturne.Core.Models.Basal;

/// <summary>
/// A piecewise-constant interval of the scheduled basal-rate timeline, expressed in absolute UTC ms.
/// Half-open: <c>[StartMills, EndMills)</c>. Within the interval the effective rate is constant.
/// </summary>
/// <remarks>
/// "Effective" rate means the schedule rate after the active CircadianPercentageProfile percentage
/// has been applied. <see cref="ScheduledUnitsPerHour"/> exposes the un-adjusted rate for diagnostic
/// or comparison views.
/// </remarks>
/// <param name="StartMills">Inclusive start of the segment, Unix ms.</param>
/// <param name="EndMills">Exclusive end of the segment, Unix ms.</param>
/// <param name="UnitsPerHour">Effective basal rate in U/hr during this segment.</param>
/// <param name="ScheduledUnitsPerHour">Pre-CCP scheduled rate, before percentage scaling.</param>
/// <param name="ProfileName">Name of the profile that supplied <see cref="ScheduledUnitsPerHour"/>.</param>
public readonly record struct BasalSegment(
    long StartMills,
    long EndMills,
    double UnitsPerHour,
    double ScheduledUnitsPerHour,
    string ProfileName)
{
    /// <summary>Segment duration in milliseconds.</summary>
    public long DurationMills => EndMills - StartMills;

    /// <summary>Total units delivered during this segment: <c>UnitsPerHour × duration</c>.</summary>
    public double Units => UnitsPerHour * (DurationMills / 3_600_000.0);
}
