using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The order of a subject's tenant list is contractual: on a host that names no tenant,
/// <c>getCurrentTenantId</c> settles the settings pages on the first entry, so it has to be a
/// tenant that person owns and it has to be the same one on every request.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServiceGetTenantsForSubjectTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public TenantServiceGetTenantsForSubjectTests()
    {
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Sam" });
        db.SaveChanges();
    }

    private TenantService Service() => new(
        _db.ContextFactory,
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        Mock.Of<ILogger<TenantService>>());

    /// <param name="role">A role seed slug, as every membership an invite creates carries one.</param>
    private void SeedMembership(string slug, DateTime joinedAt, string? role = null)
    {
        var tenantId = Guid.CreateVersion7();
        using var db = _db.CreateContext(tenantId);

        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = slug, DisplayName = slug });
        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = _subjectId,
        };
        db.TenantMembers.Add(member);

        if (role is not null)
        {
            var tenantRole = new TenantRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = role,
                Slug = role,
                Permissions = RoleSeeds.Permissions[role],
                IsSystem = true,
            };
            db.TenantRoles.Add(tenantRole);
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = member.Id,
                TenantRoleId = tenantRole.Id,
            });
        }

        db.SaveChanges();

        // Inserting stamps SysCreatedAt with the current time, so a past join date can only be
        // written afterwards.
        member.SysCreatedAt = joinedAt;
        db.SaveChanges();
    }

    /// <summary>
    /// Accepting an invite stamps a membership just as creating a tenant does, so a caregiver
    /// invited to someone else's tenant before standing up their own would otherwise be settled
    /// on the tenant they merely belong to.
    /// </summary>
    [Fact]
    public async Task GetTenantsForSubjectAsync_putsAnOwnedTenantAheadOfAnOlderInvitation()
    {
        SeedMembership("alpha", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RoleSeeds.Viewer);
        SeedMembership("zulu", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            RoleSeeds.Owner);

        var tenants = await Service().GetTenantsForSubjectAsync(_subjectId);

        tenants.Select(t => t.Slug).Should().Equal("zulu", "alpha");
    }

    [Fact]
    public async Task GetTenantsForSubjectAsync_ordersByWhenTheSubjectJoined()
    {
        SeedMembership("alpha", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedMembership("zulu", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var tenants = await Service().GetTenantsForSubjectAsync(_subjectId);

        tenants.Select(t => t.Slug).Should().Equal("zulu", "alpha");
    }

    [Fact]
    public async Task GetTenantsForSubjectAsync_breaksATieOnSlug()
    {
        var joinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedMembership("zulu", joinedAt);
        SeedMembership("alpha", joinedAt);

        var tenants = await Service().GetTenantsForSubjectAsync(_subjectId);

        tenants.Select(t => t.Slug).Should().Equal("alpha", "zulu");
    }

    public void Dispose() => _db.Dispose();
}
