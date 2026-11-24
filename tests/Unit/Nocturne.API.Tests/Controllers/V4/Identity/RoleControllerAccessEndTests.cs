using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// A caller whose own access ends on a date may change or delete only a role that no member
/// outlasting them holds, and an edit of a role the Public member holds drops the cached public access.
/// </summary>
public sealed class RoleControllerAccessEndTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CallerEnd = Now.AddDays(10);

    private readonly NocturneDbContext _dbContext = TestDbContextFactory.CreateInMemoryContext();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();
    private readonly Guid _callerMemberId = Guid.CreateVersion7();

    private readonly Guid _heldByLongerMember = Guid.CreateVersion7();
    private readonly Guid _heldByShorterMember = Guid.CreateVersion7();
    private readonly Guid _heldByNobody = Guid.CreateVersion7();
    private readonly Guid _heldByPublic = Guid.CreateVersion7();
    private readonly Guid _invitedPast = Guid.CreateVersion7();
    private readonly Guid _invitedWithin = Guid.CreateVersion7();
    private readonly Guid _invitedPastButRevoked = Guid.CreateVersion7();
    private readonly Mock<IMemberInviteRepository> _invites = new();

    public RoleControllerAccessEndTests()
    {
        _dbContext.TenantId = _tenantId;
        _dbContext.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test Tenant" });

        foreach (var (id, slug) in new[]
        {
            (_heldByLongerMember, "longer"), (_heldByShorterMember, "shorter"),
            (_heldByNobody, "nobody"), (_heldByPublic, "public-role"),
            (_invitedPast, "invited-past"), (_invitedWithin, "invited-within"), (_invitedPastButRevoked, "invited-revoked"),
        })
        {
            _dbContext.TenantRoles.Add(new TenantRoleEntity
            {
                Id = id, TenantId = _tenantId, Name = slug, Slug = slug, Permissions = [Scope.GlucoseRead],
            });
        }

        _dbContext.Subjects.Add(new SubjectEntity { Id = _callerSubjectId, Name = "Caller" });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = _callerMemberId, TenantId = _tenantId, SubjectId = _callerSubjectId, DirectPermissions = [Scope.RolesManage],
        });

        AddMember("Longer", null, _heldByLongerMember);
        AddMember("Shorter", Now.AddDays(3), _heldByShorterMember);
        AddMember(PublicSubjectFilter.PublicSubjectName, null, _heldByPublic, isSystem: true);

        _dbContext.SaveChanges();
        _dbContext.ChangeTracker.Clear();

        _invites
            .Setup(i => i.GetForTenantAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Invite(_invitedPast, null, revoked: false),
                Invite(_invitedWithin, Now.AddDays(3), revoked: false),
                Invite(_invitedPastButRevoked, null, revoked: true),
            ]);
    }

    private MemberInviteRecord Invite(Guid roleId, DateTime? accessEndsAt, bool revoked) => new(
        Guid.CreateVersion7(), _tenantId, null, _callerSubjectId, null, [roleId], null, null, false,
        Now.AddDays(7), null, 0, revoked ? Now : null, Now, [], accessEndsAt);

    private void AddMember(string name, DateTime? accessEndsAt, Guid roleId, bool isSystem = false)
    {
        var subject = new SubjectEntity { Id = Guid.CreateVersion7(), Name = name, IsSystemSubject = isSystem };
        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subject.Id,
            AccessEndsAt = accessEndsAt,
            DirectPermissions = [Scope.GlucoseRead],
        };
        _dbContext.Subjects.Add(subject);
        _dbContext.TenantMembers.Add(member);
        _dbContext.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = roleId,
        });
    }

    private RoleController BuildController(DateTime? callerEnd)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var scopes = new HashSet<string>([Scope.RolesManage, Scope.GlucoseRead]);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)scopes;
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, SubjectId = _callerSubjectId, TenantId = _tenantId,
        };
        new RequestMembership(
                _callerSubjectId,
                _tenantId,
                new MembershipFacts(_callerMemberId, new TenantMemberAccess(scopes, false, callerEnd), new TenantMemberAccess(scopes, false, callerEnd), null))
            .StoreOn(httpContext);

        return new RoleController(
            new TenantRoleService(
                new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off), new FakeTimeProvider(Now), _invites.Object,
                new Nocturne.Infrastructure.Data.Repositories.TeamRepository(_dbContext.AsFactory()), _dbContext.AsFactory(), new Nocturne.API.Services.Auth.GrantRevocationService(new Nocturne.API.Services.Auth.GuestSessionCacheService(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))),
            tenantAccessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private PublicAccessCacheService PublicAccess() => new(
        _cache,
        new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
        NullLogger<PublicAccessCacheService>.Instance);

    private async Task<PublicAccessCacheService> PrimedPublicAccessAsync()
    {
        var access = PublicAccess();
        (await access.GetPublicAccessAsync(_tenantId)).Should().NotBeNull();
        return access;
    }

    private bool PublicAccessIsCached() => _cache.TryGetValue($"public-permissions:{_tenantId}", out _);

    private static UpdateRoleRequest Edit(Guid roleId) =>
        new("Renamed " + roleId.ToString("N")[^8..], "edited", [Scope.GlucoseRead]);

    private async Task<string> RoleStateAsync(Guid roleId)
    {
        var role = await _dbContext.TenantRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId);
        return role is null ? "deleted" : role.Name + "|" + string.Join(",", role.Permissions);
    }

    private static void ShouldRefuse(IActionResult result, string detail = HttpContextExtensions.RoleHeldPastCallerDetail)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be(detail);
    }

    private Task<IActionResult> ChangeAsync(RoleController controller, string action, Guid roleId, PublicAccessCacheService access) =>
        action == "update"
            ? controller.UpdateRole(roleId, Edit(roleId), access, CancellationToken.None)
            : controller.DeleteRole(roleId, access, CancellationToken.None);

    [Theory]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    public async Task ChangingARole_byACallerWhoseAccessEnds_isRefusedWhenAMemberWhoOutlastsThemHoldsIt(string action, bool heldByPublic)
    {
        var roleId = heldByPublic ? _heldByPublic : _heldByLongerMember;
        var access = await PrimedPublicAccessAsync();
        var before = await RoleStateAsync(roleId);

        var result = await ChangeAsync(BuildController(CallerEnd), action, roleId, access);

        ShouldRefuse(result);
        (await RoleStateAsync(roleId)).Should().Be(before, "a refusal writes nothing");
        PublicAccessIsCached().Should().BeTrue("a refusal evicts nothing");
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ChangingARole_byACallerWhoseAccessEnds_isRefusedWhenALiveInviteThatOutlastsThemGivesIt(string action)
    {
        var before = await RoleStateAsync(_invitedPast);

        var result = await ChangeAsync(BuildController(CallerEnd), action, _invitedPast, PublicAccess());

        ShouldRefuse(result, HttpContextExtensions.RoleInvitedPastCallerDetail);
        (await RoleStateAsync(_invitedPast)).Should().Be(before, "a refusal writes nothing");
    }

    [Theory]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    public async Task ChangingARole_isAllowedWhenTheInviteIsWithinReachRevokedOrTheCallerHasNoEnd(string action, bool withinReach)
    {
        var roleId = withinReach ? _invitedWithin : _invitedPastButRevoked;
        var before = await RoleStateAsync(roleId);

        var result = await ChangeAsync(BuildController(CallerEnd), action, roleId, PublicAccess());

        result.Should().BeAssignableTo<IStatusCodeActionResult>().Which.StatusCode.Should().BeInRange(200, 204);
        (await RoleStateAsync(roleId)).Should().NotBe(before);

        var before2 = await RoleStateAsync(_invitedPast);
        var open = await ChangeAsync(BuildController(null), action, _invitedPast, PublicAccess());
        open.Should().BeAssignableTo<IStatusCodeActionResult>().Which.StatusCode.Should().BeInRange(200, 204);
        (await RoleStateAsync(_invitedPast)).Should().NotBe(before2);
    }

    [Theory]
    [InlineData("update", "shorter")]
    [InlineData("delete", "shorter")]
    [InlineData("update", "nobody")]
    [InlineData("delete", "nobody")]
    public async Task ChangingARole_byACallerWhoseAccessEnds_isAllowedWhenOnlyMembersWithinReachHoldIt(string action, string held)
    {
        var roleId = held == "shorter" ? _heldByShorterMember : _heldByNobody;
        var before = await RoleStateAsync(roleId);

        var result = await ChangeAsync(BuildController(CallerEnd), action, roleId, PublicAccess());

        result.Should().BeAssignableTo<IStatusCodeActionResult>().Which.StatusCode.Should().BeInRange(200, 204);
        (await RoleStateAsync(roleId)).Should().NotBe(before);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ChangingARole_byACallerWithNoEnd_isNeverRefusedForIt(string action)
    {
        var before = await RoleStateAsync(_heldByLongerMember);

        var result = await ChangeAsync(BuildController(null), action, _heldByLongerMember, PublicAccess());

        result.Should().BeAssignableTo<IStatusCodeActionResult>().Which.StatusCode.Should().BeInRange(200, 204);
        (await RoleStateAsync(_heldByLongerMember)).Should().NotBe(before);
    }

    [Fact]
    public async Task CreateRole_byACallerWhoseAccessEnds_isUnaffected()
    {
        var result = await BuildController(CallerEnd)
            .CreateRole(new CreateRoleRequest("Helpers", null, [Scope.GlucoseRead]), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task GetRoles_marksTheRolesAMemberWhoOutlastsACallerWhoseAccessEndsHolds_andHidesNone()
    {
        var result = await BuildController(CallerEnd).GetRoles(new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off), TimeProvider.System, CancellationToken.None);

        var roles = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<TenantRoleDto>>().Subject;
        roles.Should().HaveCount(7);
        roles.Where(r => r.OutlastsCaller).Select(r => r.Id)
            .Should().BeEquivalentTo([_heldByLongerMember, _heldByPublic]);
        roles.Where(r => r.InviteOutlastsCaller).Select(r => r.Id).Should().BeEquivalentTo([_invitedPast]);
    }

    [Fact]
    public async Task GetRoles_forACallerWithNoEnd_marksNone()
    {
        var result = await BuildController(null).GetRoles(new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off), TimeProvider.System, CancellationToken.None);

        var roles = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<TenantRoleDto>>().Subject;
        roles.Should().HaveCount(7).And.OnlyContain(r => !r.OutlastsCaller && !r.InviteOutlastsCaller);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ChangingARoleThePublicMemberHolds_evictsThePublicAccessCache(string action)
    {
        var access = await PrimedPublicAccessAsync();

        var result = await ChangeAsync(BuildController(null), action, _heldByPublic, access);

        result.Should().BeAssignableTo<IStatusCodeActionResult>().Which.StatusCode.Should().BeInRange(200, 204);
        PublicAccessIsCached().Should().BeFalse();
    }

    [Fact]
    public async Task UpdatingARoleThePublicMemberDoesNotHold_leavesThePublicAccessCache()
    {
        var access = await PrimedPublicAccessAsync();

        var result = await BuildController(null).UpdateRole(
            _heldByLongerMember, Edit(_heldByLongerMember), access, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        PublicAccessIsCached().Should().BeTrue();
    }

    private RoleController BuildMixedController(GrantCeiling? ceiling)
    {
        var controller = BuildController(null);
        controller.HttpContext.SetGrantCeiling(ceiling);
        return controller;
    }

    [Fact]
    public async Task MixedMember_mayNotPutAPermissionOnlyTheTeamGivesInARole()
    {
        var controller = BuildMixedController(new GrantCeiling(new HashSet<string> { Scope.RolesManage }, null, false));

        var created = await controller.CreateRole(new CreateRoleRequest("Helpers", null, [Scope.GlucoseRead]), CancellationToken.None);
        var updated = await controller.UpdateRole(_heldByNobody, Edit(_heldByNobody), PublicAccess(), CancellationToken.None);

        ShouldRefuse(created, "Cannot grant 'glucose.read' because the caller does not hold it.");
        ShouldRefuse(updated, "Cannot grant 'glucose.read' because the caller does not hold it.");
    }

    [Fact]
    public async Task MixedMember_whoseDirectReasonEnds_mayNotChangeARoleAMemberWhoOutlastsItHolds()
    {
        var controller = BuildMixedController(new GrantCeiling(new HashSet<string> { Scope.RolesManage, Scope.GlucoseRead }, CallerEnd, false));
        var before = await RoleStateAsync(_heldByLongerMember);

        ShouldRefuse(await ChangeAsync(controller, "update", _heldByLongerMember, PublicAccess()));
        (await RoleStateAsync(_heldByLongerMember)).Should().Be(before);
    }

    [Fact]
    public async Task MixedMember_mayPutWhatTheDirectReasonHoldsInARole()
    {
        var controller = BuildMixedController(new GrantCeiling(new HashSet<string> { Scope.RolesManage, Scope.GlucoseRead }, null, false));

        var result = await controller.CreateRole(new CreateRoleRequest("Helpers", null, [Scope.GlucoseRead]), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task TeamOnlyMember_givesNothing()
    {
        var controller = BuildMixedController(null);
        var before = await RoleStateAsync(_heldByNobody);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(
            await controller.CreateRole(new CreateRoleRequest("Helpers", null, [Scope.GlucoseRead]), CancellationToken.None));
        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await ChangeAsync(controller, "update", _heldByNobody, PublicAccess()));
        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await ChangeAsync(controller, "delete", _heldByNobody, PublicAccess()));
        (await RoleStateAsync(_heldByNobody)).Should().Be(before);

        var roles = (await controller.GetRoles(new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off), TimeProvider.System, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<TenantRoleDto>>().Subject;
        roles.Should().OnlyContain(r => !r.CanGrant);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _cache.Dispose();
    }
}
