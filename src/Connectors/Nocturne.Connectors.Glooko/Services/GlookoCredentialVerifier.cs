using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Verifies Glooko credentials by attempting the same sign-in the sync flow uses, via
///     <see cref="GlookoAuthTokenProvider"/>'s cache-bypassing verification path. Nothing is
///     persisted and the tenant's live session is untouched.
/// </summary>
public class GlookoCredentialVerifier(
    CredentialVerificationTokenProvider<GlookoAuthTokenProvider> tokenProvider,
    IConnectorRegistration<GlookoConnectorConfiguration> registration,
    IConnectorConfigurationService configService)
    : ConnectorCredentialVerifier<GlookoConnectorConfiguration>(registration, configService)
{
    public override string ConnectorId => "glooko";

    protected override Task<ConnectorCredentialVerificationResult> VerifyConfiguredAsync(
        GlookoConnectorConfiguration config, CancellationToken ct) =>
        tokenProvider.Provider.VerifyCredentialsAsync(config, ct);
}
