using System.Text.Json.Nodes;
using Nocturne.Core.Models.Connectors;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Stores each tenant's connector configurations: the configuration and its encrypted secrets, the
/// health a sync reports, the incremental-sync cursors and the backfill low-water marks. A connector
/// name is matched in its canonical spelling (<see cref="ConnectorNames.Canonical"/>).
/// </summary>
/// <remarks>
/// <c>connector_configurations</c> is tenant-scoped, so every method reads and writes the scope's
/// tenant, except <see cref="ListForTenantAsync"/>. Each write changes only the columns its intent
/// names, so a health report or a cursor written by a running sync never undoes a configuration save
/// that lands meanwhile.
/// </remarks>
public interface IConnectorConfigurationRepository
{
    /// <summary>The connector's configuration, or null when the tenant has none.</summary>
    Task<ConnectorConfigurationRecord?> FindAsync(string connectorName, CancellationToken ct);

    /// <summary>Every connector configuration of the tenant, by connector name.</summary>
    Task<IReadOnlyList<ConnectorConfigurationRecord>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Every connector configuration of <paramref name="tenantId"/>, by connector name, for a
    /// platform-admin caller whose scope has not resolved that tenant.
    /// </summary>
    Task<IReadOnlyList<ConnectorConfigurationRecord>> ListForTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Whether the tenant has configured any connector.</summary>
    Task<bool> AnyAsync(CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="configurationJson"/> as the connector's configuration, creating its row
    /// with no secrets when there is none.
    /// </summary>
    Task<ConnectorConfigurationRecord> SaveConfigurationAsync(
        string connectorName, string configurationJson, string? modifiedBy, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="secretsJson"/>, already encrypted, as the connector's secrets, creating
    /// its row with an empty configuration when there is none.
    /// </summary>
    Task<ConnectorConfigurationRecord> SaveSecretsAsync(
        string connectorName, string secretsJson, string? modifiedBy, CancellationToken ct);

    /// <summary>
    /// Sets the <c>enabled</c> key of the connector's configuration, keeping its other keys, and
    /// creating its row when there is none.
    /// </summary>
    Task<ConnectorConfigurationRecord> SetActiveAsync(
        string connectorName, bool isActive, string? modifiedBy, CancellationToken ct);

    /// <summary>Deletes the connector's configuration; the deleted row, or null when there was none.</summary>
    Task<ConnectorConfigurationRecord?> DeleteAsync(string connectorName, CancellationToken ct);

    /// <summary>Applies a sync's health report; the row as stored, or null when there is none.</summary>
    Task<ConnectorConfigurationRecord?> RecordHealthAsync(
        string connectorName, ConnectorHealthUpdate update, CancellationToken ct);

    /// <summary>
    /// The JSON stored under <paramref name="resource"/> in the connector's sync cursors, or null when
    /// there is none. Stored cursors that are not a JSON object read as empty.
    /// </summary>
    Task<JsonNode?> GetSyncCursorAsync(string connectorName, string resource, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="resource"/>, leaving the connector's other
    /// keys; false when the connector has no configuration.
    /// </summary>
    Task<bool> SetSyncCursorAsync(string connectorName, string resource, JsonNode value, CancellationToken ct);

    /// <summary>
    /// Removes what is stored under <paramref name="resource"/>, leaving the connector's other keys;
    /// false when the connector has no configuration.
    /// </summary>
    Task<bool> RemoveSyncCursorAsync(string connectorName, string resource, CancellationToken ct);

    /// <summary>
    /// The low-water mark of <paramref name="collection"/>, or null when none is held. The
    /// configuration is found by <paramref name="source"/>, a connector data source
    /// (<c>{connector}-connector</c>) or a bare connector name.
    /// </summary>
    Task<DateTime?> GetBackfillLowWaterMarkAsync(string source, string collection, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="lowWaterMark"/> as the low-water mark of <paramref name="collection"/>,
    /// or removes it when null, leaving the other collections' marks; false when the source has no
    /// configuration. The source is matched as <see cref="GetBackfillLowWaterMarkAsync"/> says.
    /// </summary>
    Task<bool> SetBackfillLowWaterMarkAsync(
        string source, string collection, DateTime? lowWaterMark, CancellationToken ct);
}
