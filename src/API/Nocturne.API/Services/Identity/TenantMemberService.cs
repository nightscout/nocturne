using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Provides tenant membership lookups: checking whether a subject belongs to a tenant and
/// listing all tenants a subject has access to. Uses a factory-created <see cref="NocturneDbContext"/>
/// per operation to avoid context lifetime issues in singleton-scoped callers.
/// </summary>
/// <remarks>
/// Each operation pins its context to the tenant it was asked about, so the lookup carries its
/// own RLS reach rather than inheriting the caller's. This runs on the authentication hot path
/// (<c>AuthenticationMiddleware</c>) and from SignalR and OIDC, where no ambient tenant is
/// resolved. <see cref="GetTenantIdsForSubjectAsync"/> is the one cross-tenant read and pins the
/// subject instead — it answers "which tenants does this person belong to", which no single
/// tenant pin can express.
/// </remarks>
/// <seealso cref="ITenantMemberService"/>
public class TenantMemberService : ITenantMemberService
{
    private readonly IDbContextFactory<NocturneDbContext> _factory;

    public TenantMemberService(IDbContextFactory<NocturneDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<bool> IsMemberAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateTenantPinnedContextAsync(tenantId, ct);
        return await context.TenantMembers.AsNoTracking()
            .AnyAsync(tm => tm.SubjectId == subjectId && tm.TenantId == tenantId, ct);
    }

    public async Task<List<Guid>> GetTenantIdsForSubjectAsync(Guid subjectId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateSubjectPinnedContextAsync(subjectId, ct);
        return await context.TenantMembers.AsNoTracking()
            .Where(tm => tm.SubjectId == subjectId)
            .Select(tm => tm.TenantId)
            .ToListAsync(ct);
    }

    public async Task<int> GetMemberCountAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateTenantPinnedContextAsync(tenantId, ct);
        return await context.TenantMembers.AsNoTracking()
            .CountAsync(tm => tm.TenantId == tenantId, ct);
    }

    public async Task<List<string>> GetMemberRoleNamesAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateTenantPinnedContextAsync(tenantId, ct);
        return await context.TenantMembers.AsNoTracking()
            .Where(tm => tm.SubjectId == subjectId && tm.TenantId == tenantId)
            .SelectMany(tm => tm.MemberRoles)
            .Select(mr => mr.TenantRole.Name)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<string>?> GetEffectivePermissionsAsync(
        Guid subjectId, Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateTenantPinnedContextAsync(tenantId, ct);
        var membership = await context.TenantMembers.AsNoTracking()
            .Include(tm => tm.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .Where(tm => tm.SubjectId == subjectId && tm.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (membership is null)
        {
            return null;
        }

        return membership.MemberRoles
            .SelectMany(mr => mr.TenantRole.Permissions)
            .Union(membership.DirectPermissions ?? [])
            .ToHashSet();
    }
}
