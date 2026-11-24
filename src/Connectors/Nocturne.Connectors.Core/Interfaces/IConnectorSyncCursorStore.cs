using System.Text.Json.Nodes;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     A value Google Health keeps under one key of its vendor state.
/// </summary>
public sealed record ConnectorSyncCursor(string? LastUpdatedAt, string? LastGuid);

/// <summary>
///     Google Health's keyed access to its vendor state, by connector name, for its runtime state and its
///     coordinator's work, which are written outside a sync. Every other connector keeps its state
///     through <see cref="IConnectorStateStore"/>. Scoped to the current tenant by the implementation.
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
