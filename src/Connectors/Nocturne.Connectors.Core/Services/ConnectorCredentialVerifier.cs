using System.Text.Json;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Base class for connector credential verifiers. Binds the submitted configuration and
///     secrets to a transient configuration object (never persisted), checks the required
///     properties, then delegates the live authentication attempt to the connector.
/// </summary>
public abstract class ConnectorCredentialVerifier<TConfig> : IConnectorCredentialVerifier
    where TConfig : BaseConnectorConfiguration, new()
{
    public abstract string ConnectorId { get; }

    public async Task<ConnectorCredentialVerificationResult> VerifyAsync(
        JsonDocument? configuration,
        Dictionary<string, string> secrets,
        CancellationToken ct = default)
    {
        var config = new TConfig();
        if (configuration != null)
            ConnectorConfigurationBinder.ApplyJsonToConfig(configuration, config);
        ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, config);

        var missing = config.MissingRequiredProperties();
        if (missing.Count > 0)
        {
            return ConnectorCredentialVerificationResult.Failed(
                $"Missing required fields: {string.Join(", ", missing)}");
        }

        return await VerifyConfiguredAsync(config, ct);
    }

    /// <summary>
    ///     Attempts a live authentication with a fully bound configuration. Implementations must
    ///     not persist, cache, or log the configuration's credential values.
    /// </summary>
    protected abstract Task<ConnectorCredentialVerificationResult> VerifyConfiguredAsync(
        TConfig config, CancellationToken ct);
}
