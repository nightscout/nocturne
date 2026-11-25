using System.Collections.Concurrent;

namespace Nocturne.Connectors.Nightscout.Services;

/// <summary>
///     Treatments a Nightscout connector looked up by id, per source, with when each may be looked up
///     again: one a lookup found although the read missed it, an open span read again, or one no
///     lookup could confirm gone. A treatment the read keeps missing then costs one lookup a period,
///     not one a sync. A singleton, because the connector is resolved afresh for every sync.
/// </summary>
/// <remarks>
///     Each source is pruned only by its own reads, under its own clock, so connectors whose clocks
///     disagree never remove each other's entries.
/// </remarks>
public sealed class NightscoutLookups
{
    private readonly ConcurrentDictionary<NightscoutLookupSource, ConcurrentDictionary<string, DateTime>> _bySource = new();

    public bool LookedUpRecently(NightscoutLookupSource source, string id, DateTime now)
    {
        if (!_bySource.TryGetValue(source, out var lookedUp))
            return false;

        foreach (var entry in lookedUp)
        {
            if (now > entry.Value)
                lookedUp.TryRemove(entry);
        }

        return lookedUp.ContainsKey(id);
    }

    public void Remember(NightscoutLookupSource source, string id, DateTime until) =>
        _bySource.GetOrAdd(source, _ => new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal))[id] = until;
}

/// <summary>
///     Where a treatment was looked up: a tenant's connector, on the site it reads. Tenants that read one
///     site each hold their own copy of its treatments, so a lookup for one says nothing to another.
/// </summary>
/// <param name="Tenant">The tenant the connector syncs for.</param>
/// <param name="Connector">The connector's service name.</param>
/// <param name="Site">The site's <see cref="NightscoutAddress.Base"/>.</param>
public readonly record struct NightscoutLookupSource(Guid Tenant, string Connector, string Site);
