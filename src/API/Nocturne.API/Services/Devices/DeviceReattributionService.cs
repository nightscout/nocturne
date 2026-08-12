using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Back-stamps existing unattributed records to a device the moment it is registered, so a
/// newly-declared <see cref="PatientDevice"/> immediately owns the history it explains instead of
/// waiting for new data. Registration-scoped by design — there is deliberately no global migration;
/// each registration re-runs attribution only over its own usage window.
/// </summary>
public interface IDeviceReattributionService
{
    /// <summary>
    /// Re-attributes the unattributed records within the device's usage window that its category can
    /// own, running the same matching ladder as ingest. Returns the number of records whose
    /// attribution changed.
    /// </summary>
    Task<int> ReattributeForDeviceAsync(PatientDevice device, CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class DeviceReattributionService : IDeviceReattributionService
{
    /// <summary>
    /// Upper bound on records re-stamped per registration, per record type. A multi-year CGM window
    /// can hold hundreds of thousands of rows; capping keeps the registration request bounded.
    /// Newest-first, so the most recent history is attributed; records beyond the cap in the same
    /// window stay unattributed (consistent with the deliberate no-global-migration design).
    /// </summary>
    private const int MaxReattributeRecords = 50_000;

    private readonly ISensorGlucoseRepository _sensorGlucose;
    private readonly IMeterGlucoseRepository _meterGlucose;
    private readonly IBolusRepository _boluses;
    private readonly ITempBasalRepository _tempBasals;
    private readonly IBasalInjectionRepository _basalInjections;
    private readonly IDeviceEventRepository _deviceEvents;
    private readonly IPatientDeviceStamper _stamper;
    private readonly ILogger<DeviceReattributionService> _logger;

    public DeviceReattributionService(
        ISensorGlucoseRepository sensorGlucose,
        IMeterGlucoseRepository meterGlucose,
        IBolusRepository boluses,
        ITempBasalRepository tempBasals,
        IBasalInjectionRepository basalInjections,
        IDeviceEventRepository deviceEvents,
        IPatientDeviceStamper stamper,
        ILogger<DeviceReattributionService> logger)
    {
        _sensorGlucose = sensorGlucose;
        _meterGlucose = meterGlucose;
        _boluses = boluses;
        _tempBasals = tempBasals;
        _basalInjections = basalInjections;
        _deviceEvents = deviceEvents;
        _stamper = stamper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ReattributeForDeviceAsync(PatientDevice device, CancellationToken ct = default)
    {
        // Device usage dates are local; pad ±1 day for local/UTC skew, matching the ingest stamper's
        // candidate window. Null bounds mean the device has an open-ended window.
        var from = device.StartDate is { } start ? start.ToDateTime(TimeOnly.MinValue).AddDays(-1) : (DateTime?)null;
        var to = device.EndDate is { } end ? end.ToDateTime(TimeOnly.MaxValue).AddDays(1) : (DateTime?)null;

        // A record type is in scope when the registered category is one of the categories ingest
        // lets own that type — the same map, so back-stamping and live stamping cannot diverge.
        var category = device.DeviceCategory;
        var updated = 0;

        if (DeviceAttributionCategories.SensorGlucose.Contains(category))
            updated += await BackStampAsync(_sensorGlucose, DeviceAttributionCategories.SensorGlucose, from, to, ct);

        if (DeviceAttributionCategories.MeterGlucose.Contains(category))
            updated += await BackStampAsync(_meterGlucose, DeviceAttributionCategories.MeterGlucose, from, to, ct);

        if (DeviceAttributionCategories.Bolus.Contains(category))
            updated += await BackStampAsync(_boluses, DeviceAttributionCategories.Bolus, from, to, ct);

        if (DeviceAttributionCategories.TempBasal.Contains(category))
            updated += await BackStampAsync(_tempBasals, DeviceAttributionCategories.TempBasal, from, to, ct);

        if (DeviceAttributionCategories.BasalInjection.Contains(category))
            updated += await BackStampAsync(_basalInjections, DeviceAttributionCategories.BasalInjection, from, to, ct);

        if (DeviceAttributionCategories.SensorDeviceEvent.Contains(category))
            updated += await BackStampDeviceEventsAsync(
                DeviceAttributionCategories.SensorEventTypes, DeviceAttributionCategories.SensorDeviceEvent, from, to, ct);

        if (DeviceAttributionCategories.PumpDeviceEvent.Contains(category))
            updated += await BackStampDeviceEventsAsync(
                DeviceAttributionCategories.PumpEventTypes, DeviceAttributionCategories.PumpDeviceEvent, from, to, ct);

        if (updated > 0)
            _logger.LogInformation(
                "Back-stamped {Count} record(s) after registering {Category} device {DeviceId}",
                updated, category, device.Id);

        return updated;
    }

    private async Task<int> BackStampAsync<TRecord>(
        IDeviceAttributedRepository<TRecord> repository,
        IReadOnlyList<DeviceCategory> categories,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
        where TRecord : class, IDeviceAttributed
        => await StampAndPersistAsync(
            repository, await repository.GetUnattributedAsync(from, to, MaxReattributeRecords, ct), categories, ct);

    private async Task<int> BackStampDeviceEventsAsync(
        IReadOnlyCollection<DeviceEventType> eventTypes,
        IReadOnlyList<DeviceCategory> categories,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
        => await StampAndPersistAsync(
            _deviceEvents,
            await _deviceEvents.GetUnattributedAsync(from, to, eventTypes, MaxReattributeRecords, ct),
            categories,
            ct);

    /// <summary>
    /// Re-runs the full matching ladder over all active devices of the eligible categories (including
    /// the just-registered one), then persists only the records that gained an attribution.
    /// </summary>
    private async Task<int> StampAndPersistAsync(
        IDeviceAttributionWriter writer,
        IReadOnlyList<IDeviceAttributed> unattributed,
        IReadOnlyList<DeviceCategory> categories,
        CancellationToken ct)
    {
        if (unattributed.Count == 0)
            return 0;

        await _stamper.StampAsync(unattributed, categories, batchSource: null, ct);

        var attributed = unattributed
            .Where(r => r.PatientDeviceId.HasValue)
            .ToDictionary(r => r.Id, r => r.PatientDeviceId!.Value);

        return attributed.Count == 0 ? 0 : await writer.SetPatientDeviceIdsAsync(attributed, ct);
    }
}
