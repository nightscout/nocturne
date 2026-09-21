using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Xt;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Verifies Glooko credentials by attempting the same sign-in the sync flow uses, via
///     <see cref="GlookoAuthTokenProvider"/>'s cache-bypassing verification path. Nothing is
///     persisted and the tenant's live session is untouched. On Glooko XT there is no password to
///     test — the emailed code makes the sign-in interactive — so the stored token is checked for
///     presence, expiry and acceptance by the server for a real session.
/// </summary>
public class GlookoCredentialVerifier(GlookoAuthTokenProvider tokenProvider, IGlookoXtDataClient? xtDataClient = null)
    : ConnectorCredentialVerifier<GlookoConnectorConfiguration>
{
    public override string ConnectorId => "glooko";

    protected override async Task<ConnectorCredentialVerificationResult> VerifyConfiguredAsync(
        GlookoConnectorConfiguration config, CancellationToken ct)
    {
        if (config.IsXt)
            return await VerifyXtAsync(config, ct);

        var authenticated = await tokenProvider.VerifyCredentialsAsync(config, ct);

        return authenticated
            ? ConnectorCredentialVerificationResult.Verified()
            : ConnectorCredentialVerificationResult.Failed(
                "Glooko did not accept the sign-in. Check the email, password, and server region.");
    }

    private async Task<ConnectorCredentialVerificationResult> VerifyXtAsync(GlookoConnectorConfiguration config, CancellationToken ct)
    {
        var token = config.AccessToken?.Trim();
        if (string.IsNullOrEmpty(token))
            return ConnectorCredentialVerificationResult.Failed(
                "Glooko XT is not connected yet. Use \"Connect Glooko XT\" to sign in with the emailed code.");

        if (GlookoXtJwt.TryGetExpiry(token) is { } expiry && expiry <= DateTime.UtcNow)
            return ConnectorCredentialVerificationResult.Failed(
                "The Glooko XT sign-in has expired. Sign in again with a new emailed code.");

        if (xtDataClient is null)
            return ConnectorCredentialVerificationResult.Failed("Glooko XT support is not installed on this server.");

        try
        {
            await using var session = await xtDataClient.ConnectAsync(GlookoXtConstants.ServerUrl, token, ct);
            var now = DateTime.UtcNow;
            await session.GetCollectedDataAsync(now.AddMinutes(-5), now, ct);
            return ConnectorCredentialVerificationResult.Verified();
        }
        catch (GlookoXtAuthenticationException ex)
        {
            return ConnectorCredentialVerificationResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConnectorCredentialVerificationResult.Failed($"Glooko XT could not be reached: {ex.Message}");
        }
    }
}
