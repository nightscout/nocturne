using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Which readings of a replay window are imported history: dated before the first arrival of their
/// connection (<see cref="ConnectionKeys"/>), so no live engine evaluated them as they came
/// (docs/alerts/engine-semantics.md §8). A reading of no connection is compared with the tenant's
/// earliest arrival.
/// </summary>
/// <remarks>
/// The first arrivals come from one grouped read of every stream, read with the history clamp lifted,
/// so every reader of the tenant gets the same map, and cached per tenant. The map holds connection
/// keys and instants and no reading. A write only adds later arrivals, so a cached map is stale only
/// once a connection appears, which a key missing from it shows and a fresh read replaces, or once
/// rows are deleted or restored, which <see cref="GlucoseFirstArrivalsInvalidation"/> clears. A map
/// left stale by a delete holds a first arrival earlier than the truth, so fewer readings are taken
/// for imports, which errs toward showing an outage. A history-clamped reader has no cache
/// partition, so it reads the map uncached on every request, on purpose: the map is never shared
/// across partitions.
/// </remarks>
public sealed class ImportedHistory(
    ISensorGlucoseRepository glucose, IOAuthGrantRepository grants, ITenantCache cache)
{
    private const string Key = TenantCacheFamily.GlucoseFirstArrivals + ":map";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    /// <summary>The ids of the readings dated in [<paramref name="from"/>, <paramref name="to"/>] that are imported history.</summary>
    public async Task<IReadOnlySet<Guid>> InAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var readings = (await glucose.GetStreamReadingsAsync(from, to, ct)).ToList();
        if (readings.Count == 0)
            return new HashSet<Guid>();

        var grantsById = await GrantsAsync(readings.Select(r => r.WriterGrantId), ct);
        var keyed = readings
            .Select(r => (r.Id, r.Timestamp, Key: ConnectionKeys.OwnerOf(r.WriterGrantId, r.DataSource, r.Device, grantsById).Key))
            .ToList();

        var arrivals = await cache.GetOrSetAsync(Key, () => ReadAsync(ct), Ttl, ct);
        if (keyed.Any(r => r.Key is { } key && !arrivals.ByConnection.ContainsKey(key)))
        {
            await cache.RemoveFamilyAsync(TenantCacheFamily.GlucoseFirstArrivals, ct);
            arrivals = await cache.GetOrSetAsync(Key, () => ReadAsync(ct), Ttl, ct);
        }

        return keyed
            .Where(r => (r.Key is { } key && arrivals.ByConnection.TryGetValue(key, out var first) ? first : arrivals.Earliest) is { } bound
                        && r.Timestamp < bound)
            .Select(r => r.Id)
            .ToHashSet();
    }

    private async Task<GlucoseFirstArrivals> ReadAsync(CancellationToken ct)
    {
        var streams = (await glucose.GetStreamFirstArrivalsAsync(ct)).ToList();
        var grantsById = await GrantsAsync(streams.Select(s => s.WriterGrantId), ct);
        var byConnection = new Dictionary<string, DateTime>();
        foreach (var stream in streams)
        {
            if (ConnectionKeys.OwnerOf(stream.WriterGrantId, stream.DataSource, stream.Device, grantsById).Key is not { } key)
                continue;
            byConnection[key] = byConnection.TryGetValue(key, out var first) && first < stream.FirstArrival
                ? first
                : stream.FirstArrival;
        }

        return new GlucoseFirstArrivals
        {
            ByConnection = byConnection,
            Earliest = streams.Count == 0 ? null : streams.Min(s => s.FirstArrival),
        };
    }

    private async Task<IReadOnlyDictionary<Guid, OAuthGrantRecord>> GrantsAsync(
        IEnumerable<Guid?> writers, CancellationToken ct)
    {
        var ids = writers.OfType<Guid>().ToHashSet();
        if (ids.Count == 0)
            return new Dictionary<Guid, OAuthGrantRecord>();
        return (await grants.ListForConnectionsAsync(null, ids, ct))
            .Where(ConnectionKeys.NamesConnection)
            .ToDictionary(g => g.Id);
    }
}

/// <summary>Each connection's first arrival, by <see cref="RecordOwner.Key"/>, and the tenant's earliest.</summary>
public sealed class GlucoseFirstArrivals
{
    public Dictionary<string, DateTime> ByConnection { get; init; } = [];

    public DateTime? Earliest { get; init; }
}
