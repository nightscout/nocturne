using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// One-time startup backfill that mints a share token for every tenant that was publicly readable
/// under the legacy model — its Public subject holds at least one role or direct permission — but
/// has no share token yet. After the bare {slug} host becomes login-only, this keeps those tenants'
/// public access working at the new {token}.share.{baseDomain} URL (the URL changes; operators are
/// expected to notify affected tenants). Idempotent: only tenants with a null share token are touched,
/// so it is safe to run on every startup.
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
                string token;
                do
                {
                    token = generator.Generate();
                }
                while (!used.Add(token));

                tenant.ShareToken = token;
                tenant.ShareTokenSetAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Backfilled share tokens for {Count} previously-public tenant(s)", tenants.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error backfilling share tokens for previously-public tenants");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
