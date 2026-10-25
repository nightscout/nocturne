using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Services.Docs;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Background service that periodically removes expired OAuth device codes, authorisation codes and
/// TOTP step-up tokens, and OAuth clients nothing uses, to prevent database bloat. Runs every hour.
/// </summary>
/// <remarks>
/// The <see cref="RetentionBuffer"/> provides a grace window for in-flight exchanges to complete
/// before their codes are permanently deleted.
/// <para>
/// Every <c>/register</c> call mints a client, and registration is anonymous, so a client that never
/// reached a grant, an authorisation code or a device code within <see cref="UnusedClientRetention"/>
/// is deleted. A client with a grant stays even once every grant is revoked: it names the app in the
/// grant's audit trail. The tenant's Scalar docs client is never deleted.
/// </para>
/// </remarks>
public class OAuthCodeCleanupService : BackgroundService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILogger<OAuthCodeCleanupService> _logger;

    /// <summary>
    /// Interval between cleanup runs (1 hour)
    /// </summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Buffer period after expiry before deletion (1 hour).
    /// Codes are only deleted once they have been expired for at least this long,
    /// giving clients a grace window to complete any in-flight exchanges.
    /// </summary>
    private static readonly TimeSpan RetentionBuffer = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a registered client may go without a grant or a code before it is deleted. Longer
    /// than any device code lives, so a registrant mid-flow keeps its client.
    /// </summary>
    internal static readonly TimeSpan UnusedClientRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthCodeCleanupService"/> class.
    /// </summary>
    /// <param name="contexts">Root-resolved, so each tenant's context is pinned explicitly.</param>
    /// <param name="logger">Logger</param>
    public OAuthCodeCleanupService(
        INocturneDbContextFactory contexts,
        ILogger<OAuthCodeCleanupService> logger
    )
    {
        _contexts = contexts;
        _logger = logger;
    }

    /// <summary>
    /// Execute the background service
    /// </summary>
    /// <param name="stoppingToken">Stopping token</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OAuth code cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException or RetryLimitExceededException)
            {
                _logger.LogError(ex, "OAuth code cleanup failed; retrying at the next tick");
            }
        }

        _logger.LogInformation("OAuth code cleanup service stopped");
    }

    /// <summary>
    /// Deletes the expired codes and step-up tokens, then the unused clients, tenant by tenant:
    /// the OAuth tables are tenant-scoped under FORCE ROW LEVEL SECURITY, so a context pinned to no
    /// tenant sees none of their rows.
    /// </summary>
    internal async Task CleanupAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var codeCutoff = now.Subtract(RetentionBuffer);
        var clientCutoff = now.Subtract(UnusedClientRetention);

        List<Guid> tenantIds;
        int deletedStepUpTokens;
        await using (var platform = await _contexts.CreatePlatformAsync(ct))
        {
            tenantIds = await platform.Tenants.Select(t => t.Id).ToListAsync(ct);
            deletedStepUpTokens = await platform
                .TotpStepUpTokens.Where(t => t.ExpiresAt < codeCutoff)
                .ExecuteDeleteAsync(ct);
        }

        int deletedDeviceCodes = 0, deletedAuthCodes = 0, deletedClients = 0;
        foreach (var tenantId in tenantIds)
        {
            try
            {
                await using var db = await _contexts.CreateForTenantAsync(tenantId, ct);

                deletedDeviceCodes += await db
                    .OAuthDeviceCodes.Where(d => d.ExpiresAt < codeCutoff)
                    .ExecuteDeleteAsync(ct);

                deletedAuthCodes += await db
                    .OAuthAuthorizationCodes.Where(c => c.ExpiresAt < codeCutoff)
                    .ExecuteDeleteAsync(ct);

                deletedClients += await db
                    .OAuthClients.Where(c =>
                        c.CreatedAt < clientCutoff
                        && c.SoftwareId != ScalarAuthProvider.ScalarSoftwareId
                        && !db.OAuthGrants.Any(g => g.ClientEntityId == c.Id)
                        && !db.OAuthAuthorizationCodes.Any(a => a.ClientEntityId == c.Id)
                        && !db.OAuthDeviceCodes.Any(d => d.ClientId == c.ClientId))
                    .ExecuteDeleteAsync(ct);
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException or RetryLimitExceededException)
            {
                _logger.LogError(ex, "OAuth code cleanup failed for tenant {TenantId}", tenantId);
            }
        }

        if (deletedDeviceCodes > 0 || deletedAuthCodes > 0 || deletedStepUpTokens > 0 || deletedClients > 0)
        {
            _logger.LogInformation(
                "OAuth code cleanup: removed {DeviceCodes} device codes, {AuthCodes} authorization codes, "
                    + "{StepUpTokens} TOTP step-up tokens and {Clients} unused clients",
                deletedDeviceCodes,
                deletedAuthCodes,
                deletedStepUpTokens,
                deletedClients
            );
        }
    }

    /// <summary>
    /// Stop the background service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OAuth code cleanup service is stopping");
        await base.StopAsync(cancellationToken);
    }
}
