using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Time-of-day basal insulin rate schedule (U/hr), decomposed from a legacy <see cref="Profile"/> record.
/// </summary>
/// <remarks>
/// Each entry in <see cref="Entries"/> defines the basal rate in units per hour from a given time until the
/// next entry. Together with <see cref="CarbRatioSchedule"/>, <see cref="SensitivitySchedule"/>,
/// <see cref="TargetRangeSchedule"/>, and <see cref="TherapySettings"/>, this forms the complete V4
/// profile for a named profile store. All schedules decomposed from the same legacy <see cref="Profile"/>
/// share the same <see cref="IV4Record.CorrelationId"/>.
/// </remarks>
/// <seealso cref="Profile"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="ScheduleEntry"/>
/// <seealso cref="CarbRatioSchedule"/>
/// <seealso cref="SensitivitySchedule"/>
/// <seealso cref="TargetRangeSchedule"/>
/// <seealso cref="TherapySettings"/>
/// <seealso cref="ProfileSummary"/>
/// <seealso cref="TempBasal"/>
[JsonSchemaFlatten]
public class BasalSchedule : V4RecordBase
{
    /// <summary>
    /// Named profile this schedule belongs to
    /// </summary>
    public string ProfileName { get; set; } = "Default";

    /// <summary>
    /// Basal rate entries throughout the day (time + U/hr value)
    /// </summary>
    public List<ScheduleEntry> Entries { get; set; } = [];
}
