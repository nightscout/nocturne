using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The membership store and the per-tenant scope plumbing the cross-tenant overview and review
/// tests share.
/// </summary>
internal static class TenantOverviewTestKit
{
    public static DbContextOptions<NocturneDbContext> NewOptions() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    public static Guid SeedMembership(
        DbContextOptions<NocturneDbContext> options,
        Guid subjectId,
        string slug,
        List<string>? rolePermissions,
        List<string>? directPermissions = null,
        bool tenantActive = true,
        bool limitTo24Hours = false,
        DateTime? lastReadingAt = null,
        DateTime? accessEndsAt = null)
    {
        using var db = new NocturneDbContext(options);
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = slug,
            IsActive = tenantActive,
            LastReadingAt = lastReadingAt,
        };
        db.Tenants.Add(tenant);

        if (!db.Subjects.Any(s => s.Id == subjectId))
            db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Member" });

        var member = new TenantMemberEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            SubjectId = subjectId,
            DirectPermissions = directPermissions,
            LimitTo24Hours = limitTo24Hours,
            AccessEndsAt = accessEndsAt,
        };
        db.TenantMembers.Add(member);

        if (rolePermissions is not null)
        {
            var role = new TenantRoleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = "role",
                Slug = $"role-{slug}",
                Permissions = rolePermissions,
            };
            db.TenantRoles.Add(role);
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.NewGuid(),
                TenantMemberId = member.Id,
                TenantRoleId = role.Id,
            });
        }

        db.SaveChanges();
        return tenant.Id;
    }

    /// <summary>
    /// The per-tenant scope's context factory, pinned to that scope's tenant and recording the
    /// scope's clamp when a context is taken.
    /// </summary>
    public sealed class ScopeContextFactory(
        DbContextOptions<NocturneDbContext> options,
        Func<Guid> tenantId,
        ICategoryReadContext? category = null,
        Dictionary<Guid, bool>? clampSeen = null) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
        {
            var context = new NocturneDbContext(options) { TenantId = tenantId() };
            if (clampSeen is not null)
                clampSeen[context.TenantId] = category?.IsHistoryClamped == true;
            return ValueTask.FromResult(context);
        }
    }

    public sealed class InMemoryContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
