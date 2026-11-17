using System.Text.Json;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Base class for connector credential verifiers. Binds the submitted configuration and
///     secrets over the connector's startup defaults into a transient configuration object (never
///     persisted), checks the required properties, then delegates the live authentication attempt
///     to the connector.
/// </summary>
public abstract class ConnectorCredentialVerifier<TConfig>(
    IConnectorRegistration<TConfig> registration,
    IConnectorConfigurationService configService)
    : IConnectorCredentialVerifier
    where TConfig : BaseConnectorConfiguration, new()
{
    private readonly ConnectorConfigurationReader<TConfig> _reader = new(registration, configService);

    public abstract string ConnectorId { get; }

    public async Task<ConnectorCredentialVerificationResult> VerifyAsync(
        JsonDocument? configuration,
        Dictionary<string, string> secrets,
        IReadOnlyCollection<string> storedSecrets,
        CancellationToken ct = default)
    {
        TConfig config;
        if (storedSecrets.Count == 0)
        {
            config = ConnectorConfigurationReader<TConfig>.Seed(_reader.DefaultsWithoutSecrets, configuration);
        }
        else
        {
            var secretKeys = ConnectorConfigurationBinder.SecretKeyNames(typeof(TConfig));
            foreach (var key in storedSecrets)
            {
                if (!secretKeys.Contains(key))
                    throw new InvalidVerificationRequestException($"'{key}' is not a secret of this connector.");
                if (secrets.ContainsKey(key))
                    throw new InvalidVerificationRequestException($"'{key}' is both submitted and stored.");
            }

            try
            {
                // A stored secret is only ever sent where the tenant's stored configuration already
                // sends it, so a caller cannot point it at a server of their choosing.
                var defaults = _reader.DefaultsWithoutSecrets;
                config = await _reader.ReadConfigurationAsync(defaults, ct)
                         ?? ConnectorConfigurationReader<TConfig>.Seed(defaults, null);
                if (configuration != null && ConnectorConfigurationReader<TConfig>.WouldChange(config, configuration))
                    throw new InvalidVerificationRequestException(
                        "Stored secrets can only be verified against the stored configuration.");

                await _reader.ApplySecretsAsync(config, storedSecrets, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                return ConnectorCredentialVerificationResult.Failed("The stored configuration could not be read.");
            }
        }

        ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, config);

        var missing = config.MissingRequiredProperties();
        if (missing.Count > 0)
        {
            return ConnectorCredentialVerificationResult.Failed(
                $"Missing required fields: {string.Join(", ", missing)}");
        }

        // One attempt, whatever the submitted budget says. A sync retries a provider that is
        // briefly unwell; a caller waiting on "test these credentials" wants an answer, and
        // ProductionRetryDelayStrategy's backoff starts at 30 seconds — at the default three
        // attempts a retryable 503 would hold the request for a minute and a half.
        config.MaxRetryAttempts = 0;

        return await VerifyConfiguredAsync(config, ct);
    }

    /// <summary>
    ///     Attempts a live authentication with a fully bound configuration. Implementations must
    ///     not persist, cache, or log the configuration's credential values.
    /// </summary>
    protected abstract Task<ConnectorCredentialVerificationResult> VerifyConfiguredAsync(
        TConfig config, CancellationToken ct);
}
