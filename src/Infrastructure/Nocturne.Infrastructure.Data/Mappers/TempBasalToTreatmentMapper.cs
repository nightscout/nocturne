using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting TempBasal domain model records to Treatment objects for V1-V3 API compatibility.
/// This replaces the StateSpan-based mapping with direct TempBasal-to-Treatment conversion.
/// </summary>
public static class TempBasalToTreatmentMapper
{
    /// <summary>
    /// Converts a TempBasal to a Treatment for V1-V3 API compatibility.
    /// </summary>
    /// <param name="tempBasal">The TempBasal to convert</param>
    /// <returns>A Treatment representation of the TempBasal</returns>
    public static Treatment ToTreatment(TempBasal tempBasal)
    {
        var rate = tempBasal.Origin == TempBasalOrigin.Suspended ? 0 : tempBasal.Rate;
        var durationMinutes = tempBasal.EndMills.HasValue
            ? (tempBasal.EndMills.Value - tempBasal.StartMills) / (1000.0 * 60.0)
            : 0;

        var treatment = new Treatment
        {
            // Emit a resolvable id: a caller-supplied ObjectId round-trips via LegacyId; otherwise
            // the UUID, which serializes to a range-resolvable ObjectId. Update paths re-key to the
            // stored LegacyId before re-decomposing, so PATCH updates in place.
            Id = MongoObjectId.IsObjectId(tempBasal.LegacyId)
                ? tempBasal.LegacyId
                : tempBasal.Id.ToString(),
            Mills = tempBasal.StartMills,
            EventType = "Temp Basal",
            Duration = durationMinutes,
            Absolute = rate,
            Rate = rate,
            Temp = "absolute",
            EnteredBy = tempBasal.App,
            UtcOffset = tempBasal.UtcOffset,
            DataSource = tempBasal.DataSource,
        };

        // Carry origin, scheduled rate, and device in AdditionalProperties for debug/display
        treatment.AdditionalProperties ??= new Dictionary<string, object>();
        treatment.AdditionalProperties["basalOrigin"] = tempBasal.Origin.ToString();
        if (tempBasal.ScheduledRate.HasValue)
            treatment.AdditionalProperties["scheduledRate"] = tempBasal.ScheduledRate.Value;
        if (!string.IsNullOrEmpty(tempBasal.Device))
            treatment.AdditionalProperties["device"] = tempBasal.Device;

        return treatment;
    }

    /// <summary>
    /// Converts multiple TempBasals to Treatments.
    /// </summary>
    /// <param name="tempBasals">The TempBasals to convert</param>
    /// <returns>Collection of Treatment objects</returns>
    public static IEnumerable<Treatment> ToTreatments(IEnumerable<TempBasal> tempBasals)
    {
        return tempBasals.Select(ToTreatment);
    }
}
