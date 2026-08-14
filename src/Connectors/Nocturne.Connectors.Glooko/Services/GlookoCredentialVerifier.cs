using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Verifies Glooko credentials by attempting the same sign-in the sync flow uses, via
///     <see cref="GlookoAuthTokenProvider"/>'s cache-bypassing verification path. Nothing is
///     persisted and the tenant's live session is untouched.
/// </summary>
public class GlookoCredentialVerifier : ConnectorCredentialVerifier<GlookoConnectorConfiguration>
{
    private readonly GlookoAuthTokenProvider _tokenProvider;

    public GlookoCredentialVerifier(GlookoAuthTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public override string ConnectorId => "glooko";

    protected override async Task<ConnectorCredentialVerificationResult> VerifyConfiguredAsync(
        GlookoConnectorConfiguration config, CancellationToken ct)
    {
        var authenticated = await _tokenProvider.VerifyCredentialsAsync(config, ct);

        return authenticated
            ? ConnectorCredentialVerificationResult.Verified()
            : ConnectorCredentialVerificationResult.Failed(
                "Glooko did not accept the sign-in. Check the email, password, and server region.");
    }
}
