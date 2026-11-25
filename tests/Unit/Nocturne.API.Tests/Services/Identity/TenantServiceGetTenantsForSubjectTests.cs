using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Auth;
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
using Nocturne.Infrastructure.Data.Repositories;
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
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public TenantServiceGetTenantsForSubjectTests()
    {
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Sam" });
        db.SaveChanges();
    }

    private TenantService Service() => new(
        _db.ContextFactory.AsNocturneFactory(),
        new TenantRepository(_db.ContextFactory.AsNocturneFactory()),
        new TenantMembershipRepository(_db.ContextFactory.AsNocturneFactory(), AccountLinkRoutes.Off),
        new SubjectRepository(_db.ContextFactory.AsNocturneFactory(), AccountLinkRoutes.Off),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        _clock,
        Mock.Of<ILogger<TenantService>>(), AccountLinkRoutes.Off, InertTeamStaff.Announcer());

    /// <param name="role">A role seed slug, as every membership an invite creates carries one.</param>
    private void SeedMembership(
        string slug, DateTime joinedAt, string? role = null, bool isActive = true, DateTime? accessEndsAt = null)
    {
        var tenantId = Guid.CreateVersion7();
        using var db = _db.CreateContext(tenantId);

        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = slug, DisplayName = slug, IsActive = isActive });
        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = _subjectId,
            AccessEndsAt = accessEndsAt,
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

    [Fact]
    public async Task GetReachableTenantsForSubjectAsync_leavesOutAnInactiveTenant()
    {
        SeedMembership("alpha", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            isActive: false);
        SeedMembership("zulu", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var reachable = await Service().GetReachableTenantsForSubjectAsync(_subjectId);
        var all = await Service().GetTenantsForSubjectAsync(_subjectId);

        reachable.Select(t => t.Slug).Should().Equal("zulu");
        all.Select(t => t.Slug).Should().Equal("alpha", "zulu");
    }

    [Fact]
    public async Task GetReachableTenantsForSubjectAsync_keepsTheOwnedFirstOrder()
    {
        SeedMembership("alpha", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RoleSeeds.Viewer);
        SeedMembership("zulu", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            RoleSeeds.Owner);

        var tenants = await Service().GetReachableTenantsForSubjectAsync(_subjectId);

        tenants.Select(t => t.Slug).Should().Equal("zulu", "alpha");
    }

    [Fact]
    public async Task BothListsLeaveOutATenantWhoseMembershipHasEnded()
    {
        SeedMembership("alpha", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), accessEndsAt: Now.AddSeconds(-1));
        SeedMembership("zulu", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), accessEndsAt: Now.AddSeconds(1));

        (await Service().GetTenantsForSubjectAsync(_subjectId)).Select(t => t.Slug).Should().Equal("zulu");
        (await Service().GetReachableTenantsForSubjectAsync(_subjectId)).Select(t => t.Slug).Should().Equal("zulu");
    }

    [Fact]
    public async Task TheMemberListKeepsAnEndedMemberAndSaysItHasEnded()
    {
        var tenantId = Guid.CreateVersion7();
        var ended = Now.AddSeconds(-1);
        var ahead = Now.AddSeconds(1);
        var aheadSubject = Guid.CreateVersion7();
        using (var db = _db.CreateContext(tenantId))
        {
            db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = "members", DisplayName = "Members" });
            db.Subjects.Add(new SubjectEntity { Id = aheadSubject, Name = "Ahead" });
            db.TenantMembers.AddRange(
                new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = _subjectId, AccessEndsAt = ended },
                new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = aheadSubject, AccessEndsAt = ahead });
            db.SaveChanges();
        }

        var members = (await Service().GetByIdAsync(tenantId))!.Members;

        members.Single(m => m.SubjectId == _subjectId).Should().Match<TenantMemberDto>(m => m.AccessEndsAt == ended && m.HasEnded);
        members.Single(m => m.SubjectId == aheadSubject).Should().Match<TenantMemberDto>(m => m.AccessEndsAt == ahead && !m.HasEnded);
    }

    public void Dispose() => _db.Dispose();
}
