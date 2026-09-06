using Nocturne.API.Services.Platform;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Default <see cref="ICanonicalGlucoseService"/>: detects multi-stream inputs, loads the
/// patient's devices once per scope, and delegates selection to <see cref="CanonicalGlucoseStream"/>.
/// </summary>
internal sealed class CanonicalGlucoseService : ICanonicalGlucoseService
{
    /// <summary>
    /// Newest rows consulted by <see cref="GetLatestAsync"/> — enough for several concurrent
    /// streams at 1-minute cadence to still contain the canonical winner's latest bucket.
    /// </summary>
    private const int LatestFetchLimit = 60;

    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IPatientDeviceRepository _patientDeviceRepository;
    private readonly IDemoModeService _demoMode;

    private IReadOnlyList<PatientDevice>? _devices;

    public CanonicalGlucoseService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IPatientDeviceRepository patientDeviceRepository,
        IDemoModeService demoMode)
    {
        _sensorGlucoseRepository = sensorGlucoseRepository ?? throw new ArgumentNullException(nameof(sensorGlucoseRepository));
        _patientDeviceRepository = patientDeviceRepository ?? throw new ArgumentNullException(nameof(patientDeviceRepository));
        _demoMode = demoMode ?? throw new ArgumentNullException(nameof(demoMode));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SensorGlucose>> SelectAsync(
        IReadOnlyList<SensorGlucose> readings,
        CancellationToken ct = default)
    {
        if (!IsMultiStream(readings))
            return readings;

        _devices ??= (await _patientDeviceRepository.GetAllAsync(ct)).ToList();
        return CanonicalGlucoseStream.Select(readings, _devices);
    }

    /// <inheritdoc />
    public async Task<SensorGlucose?> GetLatestAsync(CancellationToken ct = default)
    {
        // No time window: delayed-sync sources (hours-late connector chunks) must still surface
        // a latest value, matching the pre-canonical behaviour of using a write batch's newest
        // reading however old it was.
        var source = _demoMode.IsEnabled ? DataSources.DemoService : null;
        var recent = (await _sensorGlucoseRepository.GetAsync(
            from: null, to: null, device: null, source: source,
            limit: LatestFetchLimit, offset: 0, descending: true, nativeOnly: false, ct: ct)).ToList();

        if (!_demoMode.IsEnabled)
            recent = recent.Where(r => !DataSources.IsEphemeral(r.DataSource)).ToList();

        var canonical = await SelectAsync(recent, ct);
        return canonical.Count > 0 ? canonical[0] : null;
    }

    private static bool IsMultiStream(IReadOnlyList<SensorGlucose> readings)
    {
        if (readings.Count < 2) return false;
        var first = CanonicalGlucoseStream.StreamKey(readings[0]);
        for (var i = 1; i < readings.Count; i++)
        {
            if (CanonicalGlucoseStream.StreamKey(readings[i]) != first)
                return true;
        }
        return false;
    }
}
