namespace Nocturne.Connectors.NocturneRemote.Configurations;

/// <summary>
///     V4 API endpoint constants for the remote Nocturne instance.
/// </summary>
public static class NocturneRemoteConstants
{
    public const string SensorGlucose = "/api/v4/glucose/sensor";
    public const string MeterGlucose = "/api/v4/glucose/meter";
    public const string BGChecks = "/api/v4/observations/bg-checks";
    public const string Boluses = "/api/v4/insulin/boluses";
    public const string CarbIntake = "/api/v4/nutrition/carbs";
    public const string BolusCalculations = "/api/v4/insulin/calculations";
    public const string Notes = "/api/v4/observations/notes";
    public const string DeviceEvents = "/api/v4/observations/device-events";
    public const string StateSpans = "/api/v4/state-spans";
    public const string ProfileRecords = "/api/v4/profile/records";
    public const string ApsSnapshots = "/api/v4/device-status/aps";
    public const string PumpSnapshots = "/api/v4/device-status/pump";
    public const string UploaderSnapshots = "/api/v4/device-status/uploader";
    public const string Activity = "/api/v4/activity";
    public const string Foods = "/api/v4/foods";
}
