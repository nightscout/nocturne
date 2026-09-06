namespace Nocturne.Core.Models.V4;

/// <summary>
/// The <see cref="DeviceCategory"/> values eligible to own each <see cref="IDeviceAttributed"/>
/// record type. Single source of truth for ingest-time stamping and registration-time
/// back-stamping alike, so the two can never disagree about which device explains a record.
/// </summary>
/// <seealso cref="IDeviceAttributed"/>
/// <seealso cref="PatientDevice"/>
public static class DeviceAttributionCategories
{
    /// <summary>Categories eligible to own a <see cref="V4.SensorGlucose"/> reading.</summary>
    public static IReadOnlyList<DeviceCategory> SensorGlucose { get; } = [DeviceCategory.CGM];

    /// <summary>Categories eligible to own a <see cref="V4.MeterGlucose"/> reading.</summary>
    public static IReadOnlyList<DeviceCategory> MeterGlucose { get; } = [DeviceCategory.GlucoseMeter];

    /// <summary>Categories eligible to own a <see cref="V4.Bolus"/>.</summary>
    public static IReadOnlyList<DeviceCategory> Bolus { get; } =
        [DeviceCategory.InsulinPump, DeviceCategory.SmartPen];

    /// <summary>Categories eligible to own a <see cref="V4.TempBasal"/>.</summary>
    public static IReadOnlyList<DeviceCategory> TempBasal { get; } = [DeviceCategory.InsulinPump];

    /// <summary>Categories eligible to own a <see cref="V4.BasalInjection"/> (MDI long-acting dose).</summary>
    public static IReadOnlyList<DeviceCategory> BasalInjection { get; } =
        [DeviceCategory.InsulinPen, DeviceCategory.SmartPen];

    /// <summary>Categories eligible to own a sensor-lifecycle <see cref="V4.DeviceEvent"/>.</summary>
    public static IReadOnlyList<DeviceCategory> SensorDeviceEvent { get; } = [DeviceCategory.CGM];

    /// <summary>Categories eligible to own a pump-lifecycle <see cref="V4.DeviceEvent"/>.</summary>
    public static IReadOnlyList<DeviceCategory> PumpDeviceEvent { get; } = [DeviceCategory.InsulinPump];

    /// <summary>
    /// Sensor lifecycle events attribute to the CGM; every other device event (site, cannula,
    /// reservoir, battery, pod, priming, settings) is pump-originated.
    /// </summary>
    public static IReadOnlyList<DeviceEventType> SensorEventTypes { get; } =
    [
        DeviceEventType.SensorStart,
        DeviceEventType.SensorChange,
        DeviceEventType.SensorStop,
        DeviceEventType.TransmitterSensorInsert,
    ];

    /// <summary>The complement of <see cref="SensorEventTypes"/>, so every event type is classified.</summary>
    public static IReadOnlyList<DeviceEventType> PumpEventTypes { get; } =
        Enum.GetValues<DeviceEventType>().Except(SensorEventTypes).ToArray();

    /// <summary>True when the event type describes a CGM sensor's lifecycle rather than a pump's.</summary>
    public static bool IsSensorEvent(DeviceEventType eventType) => SensorEventTypes.Contains(eventType);

    /// <summary>Categories eligible to own a <see cref="V4.DeviceEvent"/> of the given type.</summary>
    public static IReadOnlyList<DeviceCategory> DeviceEvent(DeviceEventType eventType) =>
        IsSensorEvent(eventType) ? SensorDeviceEvent : PumpDeviceEvent;
}
