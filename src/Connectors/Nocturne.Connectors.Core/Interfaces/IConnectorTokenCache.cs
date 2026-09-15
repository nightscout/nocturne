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

    /// <summary>
    ///     Why the source last refused this key's credentials, or null when it last accepted them.
    ///     Recorded here because it is the other outcome of the same sign-in the session comes from,
    ///     and because a sync that never got a token has nothing else to report: with nothing to
    ///     fetch, several connectors finish looking like a successful sync that found no data.
    /// </summary>
    string? GetSignInRefusal(string connectorName, Guid tenantId);

    /// <param name="reason">Null when the source accepted the credentials.</param>
    /// <inheritdoc cref="GetSignInRefusal"/>
    void SetSignInRefusal(string connectorName, Guid tenantId, string? reason);
}
