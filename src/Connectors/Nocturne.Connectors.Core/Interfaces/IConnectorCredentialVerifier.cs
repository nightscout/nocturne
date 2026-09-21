using System.Text.Json;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Verifies connector credentials against the provider without persisting anything.
///     Registered per-connector and discovered by the configuration API at runtime; a connector
///     without a registered verifier does not support verification.
/// </summary>
public interface IConnectorCredentialVerifier
{
    /// <summary>
    ///     The connector ID used for dispatch (lowercase, e.g., "dexcom", "glooko").
    /// </summary>
    string ConnectorId { get; }

    /// <summary>
    ///     Attempts a live authentication with the submitted configuration and secrets.
    ///     The submitted values are bound to a transient configuration object only — never
    ///     stored, logged, or echoed back.
    /// </summary>
    /// <param name="configuration">The non-secret configuration values, in the same shape the configuration PUT accepts.</param>
    /// <param name="secrets">The secret values keyed by camelCase property name, in the same shape the secrets PUT accepts.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConnectorCredentialVerificationResult> VerifyAsync(
        JsonDocument? configuration,
        Dictionary<string, string> secrets,
        CancellationToken ct = default);
}
