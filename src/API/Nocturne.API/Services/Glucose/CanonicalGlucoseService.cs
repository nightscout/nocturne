using Nocturne.API.Services.Platform;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Default <see cref="ICanonicalGlucoseService"/>: canonical reads of the glucose store, demo data
/// only in demo mode and none outside it.
/// </summary>
internal sealed class CanonicalGlucoseService : ICanonicalGlucoseService
{
    /// <summary>
    /// Row cap for <see cref="GetRecentAsync"/>: callers ask for windows of minutes, so this only
    /// bounds a pathological burst of readings, never a normal window.
    /// </summary>
    private const int RecentFetchLimit = 500;

    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IDemoModeService _demoMode;

    public CanonicalGlucoseService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IDemoModeService demoMode)
    {
        _sensorGlucoseRepository = sensorGlucoseRepository ?? throw new ArgumentNullException(nameof(sensorGlucoseRepository));
        _demoMode = demoMode ?? throw new ArgumentNullException(nameof(demoMode));
    }

    /// <inheritdoc />
    public async Task<SensorGlucose?> GetLatestAsync(CancellationToken ct = default)
    {
        // No time window: delayed-sync sources (hours-late connector chunks) must still surface
        // a latest value, matching the pre-canonical behaviour of using a write batch's newest
        // reading however old it was.
        var latest = await ReadAsync(from: null, limit: 1, ct);
        return latest.Count > 0 ? latest[0] : null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SensorGlucose>> GetRecentAsync(DateTime since, CancellationToken ct = default)
        => ReadAsync(since, RecentFetchLimit, ct);

    private async Task<IReadOnlyList<SensorGlucose>> ReadAsync(DateTime? from, int limit, CancellationToken ct)
    {
        var (source, streams) = _demoMode.IsEnabled
            ? (DataSources.DemoService, GlucoseStreams.Canonical)
            : ((string?)null, GlucoseStreams.CanonicalExcludingEphemeral);
        return (await _sensorGlucoseRepository.GetAsync(
            from: from, to: null, device: null, source: source,
            limit: limit, offset: 0, descending: true, ct: ct, streams: streams)).ToList();
    }
}
