using System.Text.Json.Nodes;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     A persisted incremental-sync cursor for a single connector resource. <see cref="LastUpdatedAt"/>
///     and <see cref="LastGuid"/> are the opaque values the source returns to resume an ordered scan
///     (for Glooko SSV2, the server-side <c>updatedAt</c> watermark and tie-break guid).
/// </summary>
public sealed record ConnectorSyncCursor(string? LastUpdatedAt, string? LastGuid);

/// <summary>
///     Persists per-tenant, per-connector, per-resource incremental-sync cursors so a connector can
///     resume an ordered scan across syncs and process restarts, rather than re-scanning a window each
///     run. Scoped to the current tenant by the implementation.
/// </summary>
public interface IConnectorSyncCursorStore
{
    /// <summary>
    ///     Returns the stored cursor for <paramref name="resource"/>, or <c>null</c> if none has been
    ///     persisted yet (i.e. the resource has never completed a cursor-based sync for this tenant).
    /// </summary>
    Task<ConnectorSyncCursor?> GetAsync(string connectorName, string resource, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Persists <paramref name="cursor"/> for <paramref name="resource"/>, replacing any prior value.
    /// </summary>
    Task SetAsync(string connectorName, string resource, ConnectorSyncCursor cursor, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The JSON stored under <paramref name="resource"/> as it is, for state that is not a
    ///     <see cref="ConnectorSyncCursor"/>; <c>null</c> when none is stored.
    /// </summary>
    Task<JsonNode?> GetJsonAsync(string connectorName, string resource, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="value"/> under <paramref name="resource"/> as it is, replacing any prior value.</summary>
    Task SetJsonAsync(string connectorName, string resource, JsonNode value, CancellationToken cancellationToken = default);

    /// <summary>Removes whatever is stored under <paramref name="resource"/>, leaving the connector's other keys.</summary>
    Task RemoveAsync(string connectorName, string resource, CancellationToken cancellationToken = default);
}
