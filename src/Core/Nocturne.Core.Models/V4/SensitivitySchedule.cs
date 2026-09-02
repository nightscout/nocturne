using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Time-of-day insulin sensitivity factor (ISF) schedule (mg/dL per U), decomposed from a legacy <see cref="Profile"/> record.
/// </summary>
/// <remarks>
/// Each entry in <see cref="Entries"/> specifies how many mg/dL one unit of insulin lowers blood glucose
/// at a given time of day. All schedules decomposed from the same legacy <see cref="Profile"/>
/// share the same <see cref="IV4Record.CorrelationId"/>.
/// </remarks>
/// <seealso cref="Profile"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="ScheduleEntry"/>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="CarbRatioSchedule"/>
/// <seealso cref="TargetRangeSchedule"/>
/// <seealso cref="TherapySettings"/>
/// <seealso cref="ProfileSummary"/>
[JsonSchemaFlatten]
public class SensitivitySchedule : V4RecordBase
{
    /// <summary>
    /// Named profile this schedule belongs to
    /// </summary>
    public string ProfileName { get; set; } = "Default";

    /// <summary>
    /// ISF entries throughout the day (time + mg/dL per U value)
    /// </summary>
    public List<ScheduleEntry> Entries { get; set; } = [];
}
