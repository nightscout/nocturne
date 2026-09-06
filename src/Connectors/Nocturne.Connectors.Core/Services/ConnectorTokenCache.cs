using System.Collections.Concurrent;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Thread-safe, tenant-keyed cache for connector authentication sessions.
///     Registered as singleton. Per-tenant locking prevents duplicate auth requests.
/// </summary>
public class ConnectorTokenCache : IConnectorTokenCache
{
    private readonly ConcurrentDictionary<(string ConnectorName, Guid TenantId), ConnectorSession> _sessions = new();
    private readonly ConcurrentDictionary<(string ConnectorName, Guid TenantId), SemaphoreSlim> _locks = new();

    public Task<ConnectorSession?> GetAsync(string connectorName, Guid tenantId)
    {
        if (_sessions.TryGetValue(Key(connectorName, tenantId), out var session) && session.ExpiresAt > DateTime.UtcNow)
            return Task.FromResult<ConnectorSession?>(session);
        return Task.FromResult<ConnectorSession?>(null);
    }

    public Task SetAsync(string connectorName, Guid tenantId, ConnectorSession session)
    {
        _sessions[Key(connectorName, tenantId)] = session;
        return Task.CompletedTask;
    }

    public Task<SemaphoreSlim> GetLockAsync(string connectorName, Guid tenantId)
    {
        var semaphore = _locks.GetOrAdd(Key(connectorName, tenantId), _ => new SemaphoreSlim(1, 1));
        return Task.FromResult(semaphore);
    }

    public void Invalidate(string connectorName, Guid tenantId)
    {
        _sessions.TryRemove(Key(connectorName, tenantId), out _);
    }

    // Token providers key sessions by their PascalCase ConnectorName constant (e.g. "Eversense"),
    // while invalidation on config save passes the lowercase route name (e.g. "eversense").
    // Normalize so a credential change reliably clears the cached token regardless of casing.
    private static (string, Guid) Key(string connectorName, Guid tenantId) =>
        (connectorName.ToLowerInvariant(), tenantId);
}
