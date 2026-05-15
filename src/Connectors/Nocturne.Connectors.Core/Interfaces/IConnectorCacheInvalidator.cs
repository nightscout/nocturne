namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Implemented by tenant-keyed caches to support invalidation on config save.
/// </summary>
public interface IConnectorCacheInvalidator
{
    void Invalidate(string connectorName, Guid tenantId);
}
