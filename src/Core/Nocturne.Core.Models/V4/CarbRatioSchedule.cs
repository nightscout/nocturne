using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Time-of-day insulin-to-carb ratio schedule (g/U), decomposed from a legacy <see cref="Profile"/> record.
/// </summary>
/// <remarks>
/// Each entry in <see cref="Entries"/> specifies how many grams of carbohydrate one unit of insulin
/// covers at a given time of day. All schedules decomposed from the same legacy <see cref="Profile"/>
/// share the same <see cref="IV4Record.CorrelationId"/>.
/// </remarks>
/// <seealso cref="Profile"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="ScheduleEntry"/>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="SensitivitySchedule"/>
/// <seealso cref="TargetRangeSchedule"/>
/// <seealso cref="TherapySettings"/>
/// <seealso cref="ProfileSummary"/>
[JsonSchemaFlatten]
public class CarbRatioSchedule : V4RecordBase
{
    /// <summary>
    /// Named profile this schedule belongs to
    /// </summary>
    public string ProfileName { get; set; } = "Default";

    /// <summary>
    /// Carb ratio entries throughout the day (time + g/U value)
    /// </summary>
    public List<ScheduleEntry> Entries { get; set; } = [];
}
