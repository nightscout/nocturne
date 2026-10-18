using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Keeps the sensor starts inferred from a CGM's warm-up gaps in step with its readings: writes a
/// <see cref="DeviceEventType.SensorStart"/> for each gap that reads as a sensor change, and
/// withdraws an inferred start once its gap fills in or a recorded session event accounts for it.
/// </summary>
public interface ISensorSessionInferenceService
{
    /// <summary>
    /// Reconciles the inferred starts falling in [<paramref name="from"/>, <paramref name="to"/>) for
    /// every catalogued CGM registered over that window.
    /// </summary>
    Task InferAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Reconciles the inferred starts of <paramref name="device"/> over its registered dates, as far
    /// back as the history this service keeps, and withdraws those outside them. Run when a CGM is
    /// registered, or its dates or model are edited, so its sensor age has a past without waiting
    /// for new readings. A device that is not a catalogued CGM keeps no inferred starts.
    /// </summary>
    Task InferHistoryAsync(PatientDevice device, CancellationToken ct = default);

    /// <summary>
    /// Withdraws every inferred start of <paramref name="device"/>, before its registration is
    /// deleted and they can no longer be told from any other device's.
    /// </summary>
    Task ForgetDeviceAsync(PatientDevice device, CancellationToken ct = default);
}
