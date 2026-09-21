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

    /// <summary>
    /// Whether this configuration is currently active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When the configuration was last modified.
    /// </summary>
    public DateTimeOffset LastModified { get; set; }

    /// <summary>
    /// Who last modified the configuration.
    /// </summary>
    public string? ModifiedBy { get; set; }

    /// <summary>
    /// When the connector last attempted to sync.
    /// </summary>
    public DateTime? LastSyncAttempt { get; set; }

    /// <summary>
    /// When the connector last successfully completed a sync.
    /// </summary>
    public DateTime? LastSuccessfulSync { get; set; }

    /// <summary>
    /// The error message from the most recent failure.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// When the error occurred.
    /// </summary>
    public DateTime? LastErrorAt { get; set; }

    /// <summary>
    /// Current health status.
    /// </summary>
    public bool IsHealthy { get; set; } = true;
}

/// <summary>
/// Status information for a connector.
/// </summary>
public class ConnectorStatusInfo
{
    /// <summary>
    /// The connector name.
    /// </summary>
    public string ConnectorName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the connector is enabled in configuration.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Whether a configuration exists in the database.
    /// </summary>
    public bool HasDatabaseConfig { get; set; }

    /// <summary>
    /// Whether the connector has secrets configured.
    /// </summary>
    public bool HasSecrets { get; set; }

    /// <summary>
    /// When the configuration was last modified.
    /// </summary>
    public DateTimeOffset? LastModified { get; set; }
}

/// <summary>
/// Service for managing connector configurations stored in the database.
/// Handles merging of environment variables (secrets) with database-stored runtime configuration.
/// </summary>
public interface IConnectorConfigurationService
{
    /// <summary>
    /// Gets the configuration for a specific connector.
    /// Returns runtime configuration from the database.
    /// Secrets are never included - use GetSecretsAsync for internal connector use.
    /// </summary>
    /// <param name="connectorName">The connector name (e.g., "Dexcom")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Configuration response or null if not found</returns>
    Task<ConnectorConfigurationResponse?> GetConfigurationAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// Saves or updates runtime configuration for a connector.
    /// Only properties marked with [RuntimeConfigurable] are stored.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="configuration">Runtime configuration values as JSON</param>
    /// <param name="modifiedBy">Who is making the change</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The saved configuration</returns>
    Task<ConnectorConfigurationResponse> SaveConfigurationAsync(string connectorName, JsonDocument configuration, string? modifiedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Saves encrypted secrets for a connector.
    /// Secrets are encrypted using AES-256-GCM before storage.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="secrets">Dictionary of secret property names to plaintext values</param>
    /// <param name="modifiedBy">Who is making the change</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="InvalidOperationException">Thrown when secret encryption is not configured.</exception>
    Task SaveSecretsAsync(string connectorName, Dictionary<string, string> secrets, string? modifiedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Gets decrypted secrets for a connector (for internal use by connectors only).
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary of secret property names to plaintext values</returns>
    Task<Dictionary<string, string>> GetSecretsAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// Gets the JSON Schema for a connector's configuration.
    /// Schema is generated from the connector's configuration class attributes.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>JSON Schema document</returns>
    Task<JsonDocument> GetSchemaAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// Gets status information for all registered connectors.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of connector status information</returns>
    Task<IReadOnlyList<ConnectorStatusInfo>> GetAllConnectorStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Enables or disables a connector configuration.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="isActive">Whether the connector should be active</param>
    /// <param name="modifiedBy">Who is making the change</param>
    /// <param name="ct">Cancellation token</param>
    Task SetActiveAsync(string connectorName, bool isActive, string? modifiedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes all configuration and secrets for a connector.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if configuration was deleted, false if it didn't exist</returns>
    Task<bool> DeleteConfigurationAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// Gets the effective configuration from a running connector.
    /// This returns the actual runtime values including those resolved from environment variables.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary of property names to effective values, or null if connector is not reachable</returns>
    Task<Dictionary<string, object?>?> GetEffectiveConfigurationAsync(string connectorName, CancellationToken ct = default);

    /// <summary>
    /// Gets the health state for a connector
    /// </summary>
    /// <param name="connectorName">The name of the connector</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The health state, or null if the connector is not found</returns>
    Task<ConnectorHealthStateDto?> GetHealthStateAsync(
        string connectorName,
        CancellationToken ct = default
    );

    /// <summary>
    /// Updates the health state for a connector
    /// </summary>
    /// <param name="connectorName">The name of the connector</param>
    /// <param name="lastSyncAttempt">When the connector last attempted to sync</param>
    /// <param name="lastSuccessfulSync">When the connector last successfully completed a sync</param>
    /// <param name="lastErrorMessage">The error message from the most recent failure</param>
    /// <param name="lastErrorAt">When the error occurred</param>
    /// <param name="isHealthy">Current health status</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>A task representing the async operation</returns>
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

/// <summary>
/// Request model for setting connector active state.
/// </summary>
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
}
