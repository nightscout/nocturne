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
    /// Row cap for <see cref="GetRecentAsync"/>, newest first. Most callers ask for minutes. The alert
    /// look-back behind a reading with no value (<c>AlertReadings.LookBackAsync</c>) asks for 24 hours:
    /// past 500 readings with no value (over 8 hours of a 1-minute source) it finds no reading with a
    /// value, so the outage reads from the oldest reading it got, which errs toward signal loss.
    /// </summary>
    private const int RecentFetchLimit = 500;

    private const int WindowPageSize = 1000;

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
        var latest = await ReadAsync(from: null, to: null, limit: 1, after: null, ct);
        return latest.Count > 0 ? latest[0] : null;
    }

    /// <inheritdoc />
    public async Task<SensorGlucose?> GetLatestAsync(DateTime notAfter, CancellationToken ct = default)
    {
        var latest = await ReadAsync(from: null, to: notAfter, limit: 1, after: null, ct);
        return latest.Count > 0 ? latest[0] : null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SensorGlucose>> GetRecentAsync(DateTime since, CancellationToken ct = default)
        => ReadAsync(since, to: null, RecentFetchLimit, after: null, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SensorGlucose>> GetWindowAsync(
        DateTime since, DateTime to, CancellationToken ct = default)
    {
        var window = new List<SensorGlucose>();
        while (true)
        {
            var page = await ReadAsync(since, to, WindowPageSize, window.Count == 0 ? null : window[^1], ct);
            window.AddRange(page);
            if (page.Count < WindowPageSize)
                return window;
        }
    }

    /// <inheritdoc />
    public Task<SensorGlucose?> GetStreamPredecessorAsync(
        SensorGlucose reading, DateTime since, CancellationToken ct = default)
        => _sensorGlucoseRepository.GetStreamPredecessorAsync(reading, since, ct);

    private async Task<IReadOnlyList<SensorGlucose>> ReadAsync(
        DateTime? from, DateTime? to, int limit, SensorGlucose? after, CancellationToken ct)
    {
        var (source, streams) = _demoMode.IsEnabled
            ? (DataSources.DemoService, GlucoseStreams.Canonical)
            : ((string?)null, GlucoseStreams.CanonicalExcludingEphemeral);
        return (await _sensorGlucoseRepository.GetAsync(
            from: from, to: to, device: null, source: source,
            limit: limit, offset: 0, descending: true,
            afterTimestamp: after?.Timestamp, afterId: after?.Id, ct: ct, streams: streams)).ToList();
    }
}
