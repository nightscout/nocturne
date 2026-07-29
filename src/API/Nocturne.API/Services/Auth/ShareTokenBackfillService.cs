using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Security;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// One-time startup backfill that mints a share token for every tenant that was publicly readable
/// under the legacy model — its Public subject holds at least one role or direct permission — but
/// has no share token yet, so the bare {slug} host becoming login-only does not leave the tenant
/// with public access silently switched off. Idempotent: only tenants with a null share token are
/// touched, so it is safe to run on every startup.
///
/// It cannot restore a working link. Only the digest is stored, so the minted token's URL is
/// knowable to nobody — not even an operator with database access — and the owner has to regenerate
/// from settings to obtain one. That is why every backfilled tenant's owner is notified through
/// <see cref="IShareLinkResetNotifier"/>: without it the settings card would report public access as
/// on, for a link nothing can resolve and nobody can produce.
/// </summary>
public sealed class ShareTokenBackfillService : IHostedService
{
    private const string PublicSubjectName = "Public";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ShareTokenBackfillService> _logger;

    public ShareTokenBackfillService(
        IServiceProvider serviceProvider,
        ILogger<ShareTokenBackfillService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var generator = scope.ServiceProvider.GetRequiredService<IShareTokenGenerator>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);

            // The Public-subject membership rows are not RLS-scoped, so this cross-tenant scan is safe.
            var publicMembers = await db.TenantMembers
                .AsNoTracking()
                .Include(m => m.MemberRoles)
                .Where(m => m.Subject!.IsSystemSubject && m.Subject.Name == PublicSubjectName)
                .ToListAsync(cancellationToken);

            var publicTenantIds = publicMembers
                .Where(m => m.MemberRoles.Count > 0 || (m.DirectPermissions?.Count ?? 0) > 0)
                .Select(m => m.TenantId)
                .ToHashSet();

            if (publicTenantIds.Count == 0)
                return;

            var tenants = await db.Tenants
                .Where(t => t.ShareToken == null && publicTenantIds.Contains(t.Id))
                .ToListAsync(cancellationToken);

            if (tenants.Count == 0)
                return;

            var used = (await db.Tenants
                .Where(t => t.ShareToken != null)
                .Select(t => t.ShareToken!)
                .ToListAsync(cancellationToken)).ToHashSet();

            var now = DateTime.UtcNow;
            foreach (var tenant in tenants)
            {
                // Only the digest is stored, so the minted token is not recoverable afterwards and
                // is never logged. The owner is notified below and generates their own link.
                string digest;
                do
                {
                    digest = CredentialHash.ShareToken(generator.Generate());
                }
                while (!used.Add(digest));

                tenant.ShareToken = digest;
                tenant.ShareTokenSetAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Backfilled share tokens for {Count} previously-public tenant(s)", tenants.Count);

            // After the save, so a tenant is never told its link was reset unless the row committed.
            var notifier = scope.ServiceProvider.GetRequiredService<IShareLinkResetNotifier>();
            foreach (var tenant in tenants)
            {
                await notifier.NotifyAsync(tenant.Id, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error backfilling share tokens for previously-public tenants");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
