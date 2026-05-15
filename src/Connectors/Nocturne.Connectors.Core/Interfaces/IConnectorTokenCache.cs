using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Tenant-keyed cache for connector authentication sessions.
///     Singleton service — stores sessions keyed by (connectorName, tenantId).
/// </summary>
public interface IConnectorTokenCache : IConnectorCacheInvalidator
{
    Task<ConnectorSession?> GetAsync(string connectorName, Guid tenantId);
    Task SetAsync(string connectorName, Guid tenantId, ConnectorSession session);
    Task<SemaphoreSlim> GetLockAsync(string connectorName, Guid tenantId);
}
