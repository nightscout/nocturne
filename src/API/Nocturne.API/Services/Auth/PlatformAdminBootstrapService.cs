using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Ensures at least one platform admin exists.
/// </summary>
/// <remarks>
/// <para>
/// Priority order:
/// <list type="number">
///   <item>If <c>Platform:AdminSubjectIds</c> is configured, those subjects are granted platform admin status.</item>
///   <item>Otherwise, if no platform admin exists, the owner of the oldest tenant is granted it.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="BootstrapAsync"/> runs at startup. On a fresh install it sees an empty
/// database and grants nothing, so the setup flow calls
/// <see cref="EnsureFirstOwnerIsPlatformAdminAsync"/> once the first owner has bound a
/// credential — otherwise that owner could not reach the admin UI until the API restarted.
/// </para>
/// </remarks>
public class PlatformAdminBootstrapService
{
    private readonly IDbContextFactory<NocturneDbContext> _dbFactory;
    private readonly PlatformOptions _options;
    private readonly ILogger<PlatformAdminBootstrapService> _logger;

    /// <summary>
    /// Initialises a new <see cref="PlatformAdminBootstrapService"/>.
    /// </summary>
    /// <param name="dbFactory">Factory for subject and tenant member queries. A dedicated
    /// context keeps the bootstrap independent of any tenant pinned on a request-scoped one.</param>
    /// <param name="options">Platform configuration options, including <c>AdminSubjectIds</c>.</param>
    /// <param name="logger">Logger for the resulting grant.</param>
    public PlatformAdminBootstrapService(
        IDbContextFactory<NocturneDbContext> dbFactory,
        IOptions<PlatformOptions> options,
        ILogger<PlatformAdminBootstrapService> logger)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Grants platform admin status according to the configured priority rules.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Option 1: explicit config takes precedence
        if (_options.AdminSubjectIds.Count > 0)
        {
            var configGrants = await db.Subjects
                .Where(s => _options.AdminSubjectIds.Contains(s.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPlatformAdmin, true), cancellationToken);

            // Config wins outright, so a set of IDs that matches nothing leaves the
            // instance with no platform admin and no way into the admin UI.
            if (configGrants == 0)
                _logger.LogWarning(
                    "Platform:AdminSubjectIds lists {ConfiguredCount} subject ID(s), none of which " +
                    "matched an existing subject. No platform admin was granted.",
                    _options.AdminSubjectIds.Count);

            return;
        }

        // No-op if a platform admin already exists
        if (await db.Subjects.AnyAsync(s => s.IsPlatformAdmin, cancellationToken))
            return;

        // Option 2: grant to the owner of the oldest tenant that has one. Resolved by walking
        // tenants oldest-first and looking the owner up under each tenant's own pin, because a
        // single query over every tenant's memberships has no tenant to be pinned to.
        var firstOwnerSubjectId = await FindOldestTenantOwnerAsync(db, cancellationToken);

        if (firstOwnerSubjectId is null)
        {
            // Expected on a fresh install, where setup has not created an owner yet.
            _logger.LogInformation(
                "No platform admin exists and no tenant owner was found, so none was granted");
            return;
        }

        await GrantAsync(db, firstOwnerSubjectId.Value, cancellationToken);

        _logger.LogInformation(
            "Granted platform admin to subject {SubjectId}, the owner of the oldest tenant",
            firstOwnerSubjectId);
    }

    /// <summary>
    /// The subject holding the owner role on the oldest tenant that has one, or
    /// <see langword="null"/> when no tenant does. Tenants without an owner are skipped, matching
    /// the ordered membership scan this replaces.
    /// </summary>
    private static async Task<Guid?> FindOldestTenantOwnerAsync(
        NocturneDbContext db, CancellationToken cancellationToken)
    {
        // tenants is not tenant-scoped, so the ordering can be resolved unpinned.
        var tenantIds = await db.Tenants
            .OrderBy(t => t.SysCreatedAt)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        foreach (var tenantId in tenantIds)
        {
            await db.PinTenantAsync(tenantId, cancellationToken);

            var ownerSubjectId = await db.TenantMembers
                .Where(tm => tm.TenantId == tenantId
                    && tm.MemberRoles.Any(mr => mr.TenantRole!.Slug == TenantPermissions.SeedRoles.Owner))
                .Select(tm => tm.SubjectId)
                .FirstOrDefaultAsync(cancellationToken);

            if (ownerSubjectId != default) return ownerSubjectId;
        }

        return null;
    }

    /// <summary>
    /// Grants platform admin to the subject that has just completed first-owner setup,
    /// so a fresh install is administrable without restarting the API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declines when the operator pinned the choice through <c>Platform:AdminSubjectIds</c>,
    /// and when the instance already has a platform admin — an established instance must not
    /// hand the flag to whoever runs setup.
    /// </para>
    /// <para>
    /// The caller names the subject, but this method re-derives whether that subject is
    /// eligible rather than taking its word: the instance must still be a single-tenant
    /// install and the subject must hold that tenant's <c>owner</c> role. A privilege grant
    /// shouldn't depend on every present and future call site having checked setup state
    /// first, and the setup OIDC callback in particular is reachable anonymously.
    /// </para>
    /// </remarks>
    /// <param name="subjectId">The owner subject that just bound a credential.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the flag was granted.</returns>
    public async Task<bool> EnsureFirstOwnerIsPlatformAdminAsync(
        Guid subjectId, CancellationToken cancellationToken)
    {
        if (_options.AdminSubjectIds.Count > 0)
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        if (await db.Subjects.AnyAsync(s => s.IsPlatformAdmin, cancellationToken))
            return false;

        // More than one tenant means this is not a fresh install, whatever the caller thinks.
        var tenantIds = await db.Tenants.Select(t => t.Id).Take(2).ToListAsync(cancellationToken);
        if (tenantIds.Count != 1)
            return false;

        // The membership read runs under the sole tenant's pin: tenant_members is slated for
        // RLS policies keyed on the tenant GUC, and an unpinned read would then see no rows
        // and silently decline the grant — reintroducing the fresh-install lockout.
        await db.PinTenantAsync(tenantIds[0], cancellationToken);

        var isTenantOwner = await db.TenantMembers
            .AnyAsync(
                tm => tm.TenantId == tenantIds[0]
                    && tm.SubjectId == subjectId
                    && tm.MemberRoles.Any(mr => mr.TenantRole!.Slug == TenantPermissions.SeedRoles.Owner),
                cancellationToken);

        if (!isTenantOwner)
        {
            _logger.LogWarning(
                "Declined to grant platform admin to subject {SubjectId}: it does not hold the " +
                "owner role on the sole tenant", subjectId);
            return false;
        }

        var granted = await GrantAsync(db, subjectId, cancellationToken) > 0;

        if (granted)
            _logger.LogInformation(
                "Granted platform admin to first owner {SubjectId} on setup completion", subjectId);

        return granted;
    }

    private static Task<int> GrantAsync(
        NocturneDbContext db, Guid subjectId, CancellationToken cancellationToken) =>
        db.Subjects
            .Where(s => s.Id == subjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPlatformAdmin, true), cancellationToken);
}
