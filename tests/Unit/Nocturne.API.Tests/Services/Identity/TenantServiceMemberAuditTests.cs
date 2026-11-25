using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Chat;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The memberships <see cref="TenantService"/> writes (a platform admin adding a member, a tenant
/// created with its owner and its Public membership, a member removed) leave one audit row per role, permission and history
/// limit they grant or take away.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServiceMemberAuditTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _adminSubjectId = Guid.CreateVersion7();
    private readonly Guid _memberSubjectId = Guid.CreateVersion7();
    private readonly Guid _roleId = Guid.CreateVersion7();

    public TenantServiceMemberAuditTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _context = _db.CreateContext(_tenantId);
        _context.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test" });
        _context.Subjects.AddRange(
            new SubjectEntity { Id = _adminSubjectId, Name = "Admin" },
            new SubjectEntity { Id = _memberSubjectId, Name = "Member" });
        _context.TenantRoles.Add(new TenantRoleEntity
        {
            Id = _roleId, TenantId = _tenantId, Name = "Viewer", Slug = RoleSeeds.Viewer,
            Permissions = [Scope.GlucoseRead], IsSystem = true,
        });
        _context.SaveChanges();
        _context.ActingAs(new TestAuditActor { SubjectId = _adminSubjectId });
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private TenantService Service()
    {
        var factory = _context.AsFactory();
        return new TenantService(
            factory,
            new TenantRepository(factory),
            new TenantMembershipRepository(factory, AccountLinkRoutes.Off),
            new SubjectRepository(factory, AccountLinkRoutes.Off),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new OperatorConfiguration()),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ITenantRoleService>(),
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
            TimeProvider.System,
            Mock.Of<ILogger<TenantService>>(), AccountLinkRoutes.Off, InertTeamStaff.Announcer());
    }

    [Fact]
    public async Task AddMemberAsync_AuditsEachRolePermissionAndTheClampAgainstTheMember_AttributedToTheAdmin()
    {
        var added = await Service().AddMemberAsync(
            _tenantId, _memberSubjectId, [_roleId], [Scope.DevicesRead], limitTo24Hours: true);

        added.Should().BeTrue();
        var rows = await _context.AuthAuditLog.AsNoTracking().Where(a => a.TargetSubjectId == _memberSubjectId).ToListAsync();
        rows.Should().OnlyContain(a => a.ActorSubjectId == _adminSubjectId && a.TenantId == _tenantId);
        rows.Select(a => (a.EventType, a.DetailsJson)).Should().BeEquivalentTo(new[]
        {
            (AuthAuditEventType.MemberJoined, """{"kind":"tenant","how":"direct"}"""),
            (AuthAuditEventType.RoleAssigned, """{"role":"viewer","kind":"tenant"}"""),
            (AuthAuditEventType.RoleAssigned, """{"permission":"devices.read","kind":"tenant"}"""),
            (AuthAuditEventType.HistoryLimitChanged, """{"history_limit":"24_hours","kind":"tenant"}"""),
        });
    }

    [Fact]
    public async Task RemoveMemberAsync_AuditsEachRoleAndPermissionTheMemberLoses_AttributedToTheAdmin()
    {
        await Service().AddMemberAsync(_tenantId, _memberSubjectId, [_roleId], [Scope.DevicesRead]);
        var before = await _context.AuthAuditLog.AsNoTracking().Select(a => a.Id).ToListAsync();

        await Service().RemoveMemberAsync(_tenantId, _memberSubjectId);

        var rows = await _context.AuthAuditLog.AsNoTracking().Where(a => !before.Contains(a.Id)).ToListAsync();
        rows.Should().OnlyContain(a => (a.EventType == AuthAuditEventType.RoleRemoved || a.EventType == AuthAuditEventType.MemberRemoved)
            && a.TargetSubjectId == _memberSubjectId && a.ActorSubjectId == _adminSubjectId);
        rows.Select(a => a.DetailsJson).Should().BeEquivalentTo(
            """{"role":"viewer","kind":"tenant"}""",
            """{"permission":"devices.read","kind":"tenant"}""",
            """{"kind":"tenant","how":"removed"}""");
    }

    [Fact]
    public async Task CreateWithOwnerAsync_OnAnAnonymousRequest_NamesTheOwnerForTheirRoleAndTheSystemForThePublicMembership()
    {
        var publicSubjectId = Guid.CreateVersion7();
        _context.Subjects.Add(new SubjectEntity
        {
            Id = publicSubjectId, Name = PublicSubjectFilter.PublicSubjectName, IsSystemSubject = true,
        });
        await _context.SaveChangesAsync();
        _context.ActingAs(new TestAuditActor { AuthType = nameof(AuthType.None) });

        var created = await Service().CreateWithOwnerAsync("created", "Created", _memberSubjectId);

        var owner = await _context.AuthAuditLog.AsNoTracking()
            .Where(a => a.TenantId == created.Id && a.TargetSubjectId == _memberSubjectId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        owner.Select(a => (a.EventType, a.DetailsJson, a.ActorSubjectId, a.ActorCredential)).Should().Equal(
            (AuthAuditEventType.MemberJoined, """{"kind":"tenant","how":"setup"}""", _memberSubjectId, (string?)null),
            (AuthAuditEventType.RoleAssigned, """{"role":"owner","kind":"tenant"}""", _memberSubjectId, (string?)null));

        var publicRows = await _context.AuthAuditLog.AsNoTracking()
            .Where(a => a.TenantId == created.Id && a.TargetSubjectId == publicSubjectId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        publicRows.Select(a => (a.EventType, a.DetailsJson, a.ActorSubjectId, a.ActorCredential)).Should().Equal(
            (AuthAuditEventType.MemberJoined, """{"kind":"tenant","how":"system"}""", (Guid?)null, "system"),
            (AuthAuditEventType.HistoryLimitChanged, """{"history_limit":"24_hours","kind":"tenant"}""", (Guid?)null, "system"));
    }

    [Fact]
    public async Task AddMemberAsync_ForAnExistingMember_AuditsNothing()
    {
        await Service().AddMemberAsync(_tenantId, _memberSubjectId, [_roleId]);
        var before = await _context.AuthAuditLog.AsNoTracking().CountAsync();

        var added = await Service().AddMemberAsync(
            _tenantId, _memberSubjectId, [_roleId], [Scope.DevicesRead], limitTo24Hours: true);

        added.Should().BeFalse();
        (await _context.AuthAuditLog.AsNoTracking().CountAsync()).Should().Be(before);
    }
}
