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
        CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(CredentialAtRestStartupTask));
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();

        // Same purpose as the EF model, so payloads written here are readable through it, and
        // required rather than lenient so a missing registration fails startup instead of writing
        // secrets nothing can read afterwards.
        var protector = TotpSecretProtection.RequireProtector(services);
        await CredentialAtRestInitializationExtensions.ProtectTotpSecretsAsync(
            dataSource, protector, logger, cancellationToken);

        var notifier = services.GetRequiredService<IShareLinkRotatedNotifier>();
        var tokenGenerator = services.GetRequiredService<IShareTokenGenerator>();
        var rotatedTenantIds = await CredentialAtRestInitializationExtensions.RotatePlaintextShareTokensAsync(
            dataSource, tokenGenerator.Generate, logger, cancellationToken);

        foreach (var tenantId in rotatedTenantIds)
        {
            await notifier.NotifyAsync(tenantId, cancellationToken);
        }
    }
}
