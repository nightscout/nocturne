using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Time-of-day blood glucose target range schedule (low/high bounds in mg/dL),
/// decomposed from a legacy <see cref="Profile"/> record.
/// </summary>
/// <remarks>
/// Each entry in <see cref="Entries"/> specifies the lower and upper target glucose bounds that
/// APS algorithms aim for at a given time of day. All schedules decomposed from the same legacy
/// <see cref="Profile"/> share the same <see cref="IV4Record.CorrelationId"/>.
/// </remarks>
/// <seealso cref="Profile"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="TargetRangeEntry"/>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="CarbRatioSchedule"/>
/// <seealso cref="SensitivitySchedule"/>
/// <seealso cref="TherapySettings"/>
/// <seealso cref="ProfileSummary"/>
[JsonSchemaFlatten]
public class TargetRangeSchedule : V4RecordBase
{
    /// <summary>
    /// Named profile this schedule belongs to
    /// </summary>
    public string ProfileName { get; set; } = "Default";

    /// <summary>
    /// Target range entries throughout the day (time + low/high bounds)
    /// </summary>
    public List<TargetRangeEntry> Entries { get; set; } = [];
}
