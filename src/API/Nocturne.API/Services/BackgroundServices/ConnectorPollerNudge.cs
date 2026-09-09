using System.Collections.Concurrent;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Carries a tenant's connector-configuration change to the poller scheduling that connector, so a
/// poller that skips tenants until they are due picks the change up on its next tick rather than at
/// the end of the tenant's recheck interval. It is the cache-invalidation hook the configuration
/// service already calls on every write, so the write paths need no second plumbing.
/// </summary>
/// <remarks>
/// In-process only: on a multi-instance deployment the other instances' pollers notice within
/// <c>ConnectorBackgroundService.UnconfiguredRecheckInterval</c> instead.
/// </remarks>
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
