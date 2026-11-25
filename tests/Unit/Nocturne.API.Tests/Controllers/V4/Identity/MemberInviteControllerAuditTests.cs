using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// A change to a member's roles or direct permissions is audited against the member's subject,
/// one row per role or permission added or removed, and only once the change is stored.
/// </summary>
public sealed class MemberInviteControllerAuditTests : IDisposable
{
    private readonly NocturneDbContext _db;
    private int _expectedRows;
    private readonly PublicAccessCacheService _publicAccessCache = TestPublicAccessCache.Create();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();
    private readonly Guid _targetSubjectId = Guid.CreateVersion7();
    private readonly Guid _targetMemberId = Guid.CreateVersion7();
    private readonly TenantRoleEntity _owner;
    private readonly TenantRoleEntity _caretaker;
    private readonly TenantRoleEntity _viewer;

    public MemberInviteControllerAuditTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();
        _db.TenantId = _tenantId;
        _db.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test" });

        _owner = Role(RoleSeeds.Owner);
        _caretaker = Role(RoleSeeds.Caretaker);
        _viewer = Role(RoleSeeds.Viewer);
        _db.TenantRoles.AddRange(_owner, _caretaker, _viewer);

        _db.Subjects.AddRange(
            new SubjectEntity { Id = _callerSubjectId, Name = "Caller" },
            new SubjectEntity { Id = _targetSubjectId, Name = "Target" });
        _db.TenantMembers.AddRange(
            new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _callerSubjectId,
                DirectPermissions = [Scope.FullAccess],
            },
            new TenantMemberEntity
            {
                Id = _targetMemberId, TenantId = _tenantId, SubjectId = _targetSubjectId,
                DirectPermissions = [Scope.GlucoseRead, Scope.TreatmentsRead],
            });
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _targetMemberId, TenantRoleId = _viewer.Id,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _db.ActingAs(new TestAuditActor
        {
            SubjectId = _callerSubjectId, IpAddress = "203.0.113.7", UserAgent = "Browser/1.0", TraceId = "trace-1",
        });
    }

    public void Dispose() => _db.Dispose();

    private TenantRoleEntity Role(string slug) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = _tenantId, Name = RoleSeeds.DisplayNames[slug], Slug = slug,
        Permissions = [.. RoleSeeds.Permissions[slug]], IsSystem = true,
    };

    private MemberInviteController Controller(params string[] grantedScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, SubjectId = _callerSubjectId, TenantId = _tenantId,
        };

        return new MemberInviteController(
            Mock.Of<IMemberInviteService>(),
            TestTenantService.Over(_db),
            new TenantRoleService(new TenantMembershipRepository(_db.AsFactory(), AccountLinkRoutes.Off), TimeProvider.System, Moq.Mock.Of<Nocturne.Core.Contracts.Identity.IMemberInviteRepository>(), new Nocturne.Infrastructure.Data.Repositories.TeamRepository(_db.AsFactory()), _db.AsFactory(), new Nocturne.API.Services.Auth.GrantRevocationService(new Nocturne.API.Services.Auth.GuestSessionCacheService(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))),
            Mock.Of<ITenantMemberService>(),
            tenantAccessor.Object,
            new TenantMembershipRepository(_db.AsFactory(), AccountLinkRoutes.Off))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private Task<IActionResult> SetRoles(MemberInviteController controller, params Guid[] roleIds) =>
        controller.SetMemberRoles(
            _targetMemberId, new SetMemberRolesRequest([.. roleIds]), _publicAccessCache, CancellationToken.None);

    private Task<IActionResult> SetPermissions(MemberInviteController controller, params string[]? permissions) =>
        controller.SetMemberPermissions(
            _targetMemberId, new SetMemberPermissionsRequest(permissions?.ToList()), _publicAccessCache,
            CancellationToken.None);

    private void VerifyLogged(string eventType, string detailsJson)
    {
        var rows = _db.AuthAuditLog.AsNoTracking()
            .Where(a => a.EventType == eventType && a.DetailsJson == detailsJson).ToList();
        rows.Should().ContainSingle();
        var row = rows.Single();
        row.TargetSubjectId.Should().Be(_targetSubjectId);
        row.ActorSubjectId.Should().Be(_callerSubjectId);
        row.ActorCredential.Should().BeNull();
        row.TenantId.Should().Be(_tenantId);
        row.IpAddress.Should().Be("203.0.113.7");
        row.UserAgent.Should().Be("Browser/1.0");
        row.TraceId.Should().Be("trace-1");
        row.Success.Should().BeTrue();
        _expectedRows++;
    }

    private void VerifyNoOtherRows() => _db.AuthAuditLog.AsNoTracking().Count().Should().Be(_expectedRows);

    [Fact]
    public async Task SetMemberRoles_AuditsEachRoleRemovedAndEachRoleAdded()
    {
        var result = await SetRoles(Controller(Scope.FullAccess), _owner.Id, _caretaker.Id);

        result.Should().BeOfType<NoContentResult>();
        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"role":"viewer","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"owner","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"caretaker","kind":"tenant"}""");
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsOnlyTheRoleThatChanged()
    {
        await SetRoles(Controller(Scope.FullAccess), _viewer.Id, _caretaker.Id);

        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"caretaker","kind":"tenant"}""");
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsNothingWhenTheSetIsUnchanged()
    {
        var result = await SetRoles(Controller(Scope.FullAccess), _viewer.Id);

        result.Should().BeOfType<NoContentResult>();
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsNothingWhenTheLastOwnerWouldBeRemoved()
    {
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _targetMemberId, TenantRoleId = _owner.Id,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await SetRoles(Controller(Scope.FullAccess), _viewer.Id);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsNothingWhenTheCallerCannotGrantTheRole()
    {
        var result = await SetRoles(Controller(Scope.MembersManage), _viewer.Id, _owner.Id);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsEachPermissionRemovedAndEachPermissionAdded()
    {
        var result = await SetPermissions(Controller(Scope.FullAccess), Scope.TreatmentsRead, Scope.DevicesRead);

        result.Should().BeOfType<NoContentResult>();
        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"permission":"glucose.read","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"permission":"devices.read","kind":"tenant"}""");
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsEachPermissionRemovedWhenTheyAreCleared()
    {
        await SetPermissions(Controller(Scope.FullAccess), permissions: null);

        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"permission":"glucose.read","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"permission":"treatments.read","kind":"tenant"}""");
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsNothingWhenTheSetIsUnchanged()
    {
        var result = await SetPermissions(Controller(Scope.FullAccess), Scope.TreatmentsRead, Scope.GlucoseRead);

        result.Should().BeOfType<NoContentResult>();
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsNothingWhenTheCallerCannotGrantThePermission()
    {
        var result = await SetPermissions(Controller(Scope.MembersManage), Scope.DevicesRead);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        VerifyNoOtherRows();
    }

    private Task<IActionResult> SetClamp(MemberInviteController controller, bool limit) =>
        controller.SetMemberLimitTo24Hours(
            _targetMemberId, new SetMemberLimitTo24HoursRequest(limit), _publicAccessCache, CancellationToken.None);

    [Fact]
    public async Task SetMemberLimitTo24Hours_AuditsTheClampBeingSet()
    {
        var result = await SetClamp(Controller(Scope.FullAccess), true);

        result.Should().BeOfType<NoContentResult>();
        VerifyLogged(AuthAuditEventType.HistoryLimitChanged, """{"history_limit":"24_hours","kind":"tenant"}""");
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberLimitTo24Hours_AuditsTheClampBeingLifted()
    {
        (await _db.TenantMembers.SingleAsync(m => m.Id == _targetMemberId)).LimitTo24Hours = true;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await SetClamp(Controller(Scope.FullAccess), false);

        result.Should().BeOfType<NoContentResult>();
        VerifyLogged(AuthAuditEventType.HistoryLimitChanged, """{"history_limit":"full","kind":"tenant"}""");
        VerifyNoOtherRows();
    }

    [Fact]
    public async Task SetMemberLimitTo24Hours_AuditsNothingWhenTheClampIsUnchanged()
    {
        var result = await SetClamp(Controller(Scope.FullAccess), false);

        result.Should().BeOfType<NoContentResult>();
        VerifyNoOtherRows();
    }
}
