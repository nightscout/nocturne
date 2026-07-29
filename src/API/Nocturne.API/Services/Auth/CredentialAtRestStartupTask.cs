using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Security;
using Npgsql;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Runs the one-pass conversions of credential columns to their at-rest storage format, and
/// notifies the owner of every tenant whose share link was retired by that pass.
/// </summary>
/// <remarks>
/// Invoked inline during startup, before the server accepts requests, rather than as an
/// <see cref="IHostedService"/>: hosted services start after Kestrel, so a login or a share-link
/// request arriving mid-pass would meet a column in the old format and fail.
/// </remarks>
public static class CredentialAtRestStartupTask
{
    /// <summary>
    /// Encrypts pre-existing TOTP secrets, rotates pre-existing plaintext share tokens, and
    /// notifies the affected tenants' owners.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();

        // The same factory the EF model uses, so payloads written here are readable through it.
        var protector = TotpSecretProtection.CreateProtector(services);
        await CredentialAtRestInitializationExtensions.ProtectTotpSecretsAsync(
            dataSource, protector, logger, cancellationToken);

        var notifier = services.GetRequiredService<IShareLinkResetNotifier>();
        var tokenGenerator = services.GetRequiredService<IShareTokenGenerator>();
        var rotatedTenantIds = await CredentialAtRestInitializationExtensions.RotatePlaintextShareTokensAsync(
            dataSource, tokenGenerator.Generate, logger, cancellationToken);

        foreach (var tenantId in rotatedTenantIds)
        {
            await notifier.NotifyAsync(tenantId, cancellationToken);
        }
    }

}
