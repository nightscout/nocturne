using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Back-stamps existing unattributed readings to a device the moment it is registered, so a
/// newly-declared <see cref="PatientDevice"/> immediately owns the history it explains instead of
/// waiting for new data. Registration-scoped by design — there is deliberately no global migration;
/// each registration re-runs attribution only over its own usage window.
/// </summary>
public interface IDeviceReattributionService
{
    /// <summary>
    /// Re-attributes unattributed sensor glucose within the device's usage window, running the same
    /// matching ladder as ingest. Returns the number of readings whose attribution changed.
    /// </summary>
    Task<int> ReattributeForDeviceAsync(PatientDevice device, CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class DeviceReattributionService : IDeviceReattributionService
{
    /// <summary>
    /// Upper bound on readings re-stamped per registration. A multi-year CGM window can hold hundreds
    /// of thousands of rows; capping keeps the registration request bounded. Newest-first, so the most
    /// recent history is attributed; readings beyond the cap in the same window stay unattributed
    /// (consistent with the deliberate no-global-migration design).
    /// </summary>
    private const int MaxReattributeReadings = 50_000;

    private readonly ISensorGlucoseRepository _sensorGlucose;
    private readonly IPatientDeviceStamper _stamper;
    private readonly ILogger<DeviceReattributionService> _logger;

    public DeviceReattributionService(
        ISensorGlucoseRepository sensorGlucose,
        IPatientDeviceStamper stamper,
        ILogger<DeviceReattributionService> logger)
    {
        _sensorGlucose = sensorGlucose;
        _stamper = stamper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ReattributeForDeviceAsync(PatientDevice device, CancellationToken ct = default)
    {
        // Glucose stream only. Treatment (pump) re-attribution is a separate follow-up; a CGM
        // registration cannot own bolus/basal history.
        if (device.DeviceCategory != DeviceCategory.CGM)
            return 0;

        // Device usage dates are local; pad ±1 day for local/UTC skew, matching the ingest stamper's
        // candidate window. Null bounds mean the device has an open-ended window.
        var from = device.StartDate is { } start ? start.ToDateTime(TimeOnly.MinValue).AddDays(-1) : (DateTime?)null;
        var to = device.EndDate is { } end ? end.ToDateTime(TimeOnly.MaxValue).AddDays(1) : (DateTime?)null;

        var unattributed = await _sensorGlucose.GetUnattributedAsync(from, to, MaxReattributeReadings, ct);
        if (unattributed.Count == 0)
            return 0;

        // Re-run the full matching ladder over all active devices (including the just-registered one),
        // then persist only the readings that gained an attribution.
        await _stamper.StampAsync(unattributed, [DeviceCategory.CGM], batchSource: null, ct);

        var attributed = unattributed
            .Where(r => r.PatientDeviceId.HasValue)
            .ToDictionary(r => r.Id, r => r.PatientDeviceId!.Value);
        if (attributed.Count == 0)
            return 0;

        var updated = await _sensorGlucose.SetPatientDeviceIdsAsync(attributed, ct);
        _logger.LogInformation(
            "Back-stamped {Count} sensor glucose reading(s) after registering device {DeviceId}", updated, device.Id);
        return updated;
    }
}
