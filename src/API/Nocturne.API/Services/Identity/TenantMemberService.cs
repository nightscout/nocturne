using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Provides tenant membership lookups: checking whether a subject belongs to a tenant and
/// listing all tenants a subject has access to. Uses a factory-created <see cref="NocturneDbContext"/>
/// per operation to avoid context lifetime issues in singleton-scoped callers.
/// </summary>
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
        await using var context = await _factory.CreateDbContextAsync(ct);
        return await context.TenantMembers.AsNoTracking()
            .AnyAsync(tm => tm.SubjectId == subjectId && tm.TenantId == tenantId, ct);
    }

    public async Task<List<Guid>> GetTenantIdsForSubjectAsync(Guid subjectId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        return await context.TenantMembers.AsNoTracking()
            .Where(tm => tm.SubjectId == subjectId)
            .Select(tm => tm.TenantId)
            .ToListAsync(ct);
    }

    public async Task<int> GetMemberCountAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        return await context.TenantMembers.AsNoTracking()
            .CountAsync(tm => tm.TenantId == tenantId, ct);
    }

    public async Task<List<string>> GetMemberRoleNamesAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        return await context.TenantMembers.AsNoTracking()
            .Where(tm => tm.SubjectId == subjectId && tm.TenantId == tenantId)
            .SelectMany(tm => tm.MemberRoles)
            .Select(mr => mr.TenantRole.Name)
            .Distinct()
            .ToListAsync(ct);
    }
}
