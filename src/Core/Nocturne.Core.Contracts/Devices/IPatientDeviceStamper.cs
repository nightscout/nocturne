using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Stamps <see cref="IDeviceAttributed.PatientDeviceId"/> on ingested records by matching each
/// record's timestamp against the usage windows of the patient's registered devices.
/// </summary>
/// <seealso cref="IDeviceAttributed"/>
/// <seealso cref="PatientDevice"/>
public interface IPatientDeviceStamper
{
    /// <summary>
    /// Attributes each unstamped record to a <see cref="PatientDevice"/> of one of the given
    /// categories, or leaves it null when no registered device matches or the match is ambiguous.
    /// Failures are logged and swallowed — records proceed without a device link.
    /// </summary>
    /// <param name="records">Records to stamp; already-stamped records are skipped.</param>
    /// <param name="categories">Device categories eligible to receive the records.</param>
    /// <param name="batchSource">Data source of the batch, used when a record carries no DataSource.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StampAsync(
        IReadOnlyList<IDeviceAttributed> records,
        IReadOnlyList<DeviceCategory> categories,
        string? batchSource,
        CancellationToken ct = default);
}
