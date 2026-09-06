using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Maps each V4 domain model type to its broadcast category and <c>recordType</c> discriminator.
/// </summary>
/// <remarks>
/// The treatment family (Bolus, CarbIntake, BG check, calculations, basal, notes, device events) rides
/// the <c>care</c> category despite some names; the glucose family rides <c>glucose</c>; device snapshots
/// ride <c>device</c>. The <c>therapy</c> category (schedules/therapy settings) is intentionally
/// unmapped — defined and subscribable but dormant until wired — so those writes broadcast nothing.
/// </remarks>
internal static class V4BroadcastMap
{
    private static readonly Dictionary<Type, (string Category, string RecordType)> Map = new()
    {
        [typeof(SensorGlucose)] = (RealtimeCategories.Glucose, "sensorGlucose"),
        [typeof(MeterGlucose)] = (RealtimeCategories.Glucose, "meterGlucose"),
        [typeof(Calibration)] = (RealtimeCategories.Glucose, "calibration"),

        [typeof(Bolus)] = (RealtimeCategories.Care, "bolus"),
        [typeof(CarbIntake)] = (RealtimeCategories.Care, "carbIntake"),
        [typeof(BGCheck)] = (RealtimeCategories.Care, "bgCheck"),
        [typeof(BolusCalculation)] = (RealtimeCategories.Care, "bolusCalculation"),
        [typeof(BasalInjection)] = (RealtimeCategories.Care, "basalInjection"),
        [typeof(TempBasal)] = (RealtimeCategories.Care, "tempBasal"),
        [typeof(Note)] = (RealtimeCategories.Care, "note"),
        [typeof(DeviceEvent)] = (RealtimeCategories.Care, "deviceEvent"),

        [typeof(ApsSnapshot)] = (RealtimeCategories.Device, "apsSnapshot"),
        [typeof(PumpSnapshot)] = (RealtimeCategories.Device, "pumpSnapshot"),
        [typeof(UploaderSnapshot)] = (RealtimeCategories.Device, "uploaderSnapshot"),
    };

    /// <summary>Look up the category + recordType for a V4 model type. False when the type is unmapped (dormant).</summary>
    public static bool TryGet(Type modelType, out string category, out string recordType)
    {
        if (Map.TryGetValue(modelType, out var entry))
        {
            (category, recordType) = entry;
            return true;
        }

        category = recordType = string.Empty;
        return false;
    }
}
