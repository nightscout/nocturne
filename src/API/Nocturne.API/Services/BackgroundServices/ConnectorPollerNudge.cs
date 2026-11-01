using System.Collections.Concurrent;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Carries a tenant's connector-configuration change to that connector's poller, which makes the
/// tenant's job ledger row due a minute after the write, so the change is read on that run rather
/// than at the end of the tenant's interval or park. It is the cache-invalidation hook the configuration service already
/// calls on every write, so the write paths need no second plumbing.
/// </summary>
public sealed class ConnectorPollerNudge : IConnectorCacheInvalidator
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<Action<Guid>>> _subscribers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers <paramref name="onChanged"/> to run with the tenant id whenever that connector's configuration is written.</summary>
    public void Subscribe(string connectorName, Action<Guid> onChanged) =>
        _subscribers.GetOrAdd(connectorName, _ => []).Add(onChanged);

    /// <summary>Whether any poller has subscribed under <paramref name="connectorName"/>.</summary>
    public bool HasSubscribers(string connectorName) =>
        _subscribers.TryGetValue(connectorName, out var subscribers) && !subscribers.IsEmpty;

    /// <inheritdoc />
    public void Invalidate(string connectorName, Guid tenantId)
    {
        if (!_subscribers.TryGetValue(connectorName, out var subscribers))
            return;

        foreach (var subscriber in subscribers)
            subscriber(tenantId);
    }
}
