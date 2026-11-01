using System.Text.Json;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Response model for connector configuration retrieval.
/// Contains both runtime configuration (from DB) and schema information.
/// Secrets are never included in responses.
/// </summary>
public class ConnectorConfigurationResponse
{
    /// <summary>
    /// The connector name (e.g., "Dexcom", "Glooko", "LibreLinkUp").
    /// </summary>
    public string ConnectorName { get; set; } = string.Empty;

    /// <summary>
    /// Runtime configuration values as JSON.
    /// Only includes properties marked with [RuntimeConfigurable].
    /// Secrets are excluded.
    /// </summary>
    public JsonDocument Configuration { get; set; } = JsonDocument.Parse("{}");

    /// <summary>
    /// Schema version for migration support.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset LastModified { get; set; }

    /// <summary>
    /// Who last modified the configuration.
    /// </summary>
    public string? ModifiedBy { get; set; }

    public DateTime? LastSyncAttempt { get; set; }

    /// <summary>
    /// When the connector last successfully completed a sync.
    /// </summary>
    public DateTime? LastSuccessfulSync { get; set; }

    /// <summary>
    /// The error message from the most recent failure.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    public DateTime? LastErrorAt { get; set; }

    public bool IsHealthy { get; set; } = true;
}

public class ConnectorStatusInfo
{
    public string ConnectorName { get; set; } = string.Empty;

    /// <summary>
    /// The <c>enabled</c> flag in the stored <c>ConfigurationJson</c>, which
    /// <see cref="IConnectorConfigurationService.SetActiveAsync"/> writes; true when the key is
    /// absent, false when there is no stored configuration.
    /// </summary>
    public bool IsEnabled { get; set; }

    public bool HasDatabaseConfig { get; set; }

    public bool HasSecrets { get; set; }

    public DateTimeOffset? LastModified { get; set; }
}

/// <summary>
/// Service for managing connector configurations stored in the database.
/// Handles merging of environment variables (secrets) with database-stored runtime configuration.
/// </summary>
public interface IConnectorConfigurationService
{
    /// <summary>
    /// The runtime configuration stored in the database, never including secrets; connectors read
    /// those through <see cref="GetSecretsAsync"/>.
    /// </summary>
    /// <param name="connectorName">The connector name (e.g., "Dexcom")</param>
    /// <returns>Configuration response or null if not found</returns>
    Task<ConnectorConfigurationResponse?> GetConfigurationAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// Saves or updates runtime configuration for a connector.
    /// Only properties marked with [RuntimeConfigurable] are stored. A stored row keeps its enabled
    /// flag whatever the document carries, and a row with none takes the document's
    /// (<see cref="ConnectorEnabledKey"/>); <see cref="SetActiveAsync"/> is the way to change it.
    /// </summary>
    /// <param name="configuration">Runtime configuration values as JSON</param>
    /// <param name="modifiedBy">Who is making the change</param>
    /// <returns>The saved configuration</returns>
    Task<ConnectorConfigurationResponse> SaveConfigurationAsync(string connectorName, JsonDocument configuration, string? modifiedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Saves encrypted secrets for a connector.
    /// Secrets are encrypted using AES-256-GCM before storage.
    /// </summary>
    /// <param name="secrets">Dictionary of secret property names to plaintext values</param>
    /// <param name="modifiedBy">Who is making the change</param>
    /// <exception cref="InvalidOperationException">Thrown when secret encryption is not configured.</exception>
    Task SaveSecretsAsync(string connectorName, Dictionary<string, string> secrets, string? modifiedBy = null, CancellationToken ct = default);

    /// <summary>Decrypted secrets, for connectors' internal use only.</summary>
    /// <returns>Dictionary of secret property names to plaintext values</returns>
    Task<Dictionary<string, string>> GetSecretsAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// JSON Schema generated from the attributes on the connector's configuration class.
    /// </summary>
    /// <returns>JSON Schema document</returns>
    Task<JsonDocument> GetSchemaAsync(string connectorName, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectorStatusInfo>> GetAllConnectorStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Enables or disables a connector configuration.
    /// </summary>
    /// <param name="isActive">Whether the connector should be active</param>
    /// <param name="modifiedBy">Who is making the change</param>
    Task SetActiveAsync(string connectorName, bool isActive, string? modifiedBy = null, CancellationToken ct = default);

    /// <returns>True if configuration was deleted, false if it didn't exist</returns>
    Task<bool> DeleteConfigurationAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// The values a running connector actually uses, including those resolved from environment
    /// variables.
    /// </summary>
    /// <returns>Dictionary of property names to effective values, or null if connector is not reachable</returns>
    Task<Dictionary<string, object?>?> GetEffectiveConfigurationAsync(string connectorName, CancellationToken ct = default);

    /// <returns>The health state, or null if the connector is not found</returns>
    Task<ConnectorHealthStateDto?> GetHealthStateAsync(
        string connectorName,
        CancellationToken ct = default
    );

    /// <param name="lastSyncAttempt">When the connector last attempted to sync</param>
    /// <param name="lastSuccessfulSync">When the connector last successfully completed a sync</param>
    /// <param name="lastErrorMessage">The error message from the most recent failure</param>
    /// <param name="lastErrorAt">When the error occurred</param>
    /// <param name="isHealthy">Current health status</param>
    Task UpdateHealthStateAsync(
        string connectorName,
        DateTime? lastSyncAttempt = null,
        DateTime? lastSuccessfulSync = null,
        string? lastErrorMessage = null,
        DateTime? lastErrorAt = null,
        bool? isHealthy = null,
        CancellationToken ct = default
    );
}

public class SetActiveRequest
{
    /// <summary>
    /// Whether the connector should be active.
    /// </summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// Request model for verifying connector credentials. Carries the same field shapes the
/// configuration and secrets PUT endpoints accept; the values are used for a single live
/// authentication attempt and are never persisted.
/// </summary>
public class VerifyConnectorCredentialsRequest
{
    /// <summary>
    /// The non-secret configuration values, keyed by camelCase property name.
    /// </summary>
    public JsonDocument? Configuration { get; set; }

    /// <summary>
    /// The secret values (passwords, tokens), keyed by camelCase property name.
    /// </summary>
    public Dictionary<string, string>? Secrets { get; set; }

    /// <summary>
    /// The camelCase secret keys to verify from the tenant's stored secrets, so a masked secret
    /// need not be typed again. Verifies the stored configuration; a submitted configuration must
    /// match it.
    /// </summary>
    public List<string>? StoredSecrets { get; set; }
}
