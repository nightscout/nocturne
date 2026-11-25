using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The member management endpoints for a member who holds a team's reason: one with no direct reason
/// is refused with 409 <c>member_access_from_team</c> and nothing is written; one with both has only
/// its direct reason edited, checked against what the direct reason itself gives. A role grant is
/// checked as the role service does: every permission of every role within the granter's scopes.
/// </summary>
public sealed class MemberInviteControllerTeamReasonTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid Caller = Guid.CreateVersion7();
    private static readonly Guid ViewerRole = Guid.CreateVersion7();
    private static readonly Guid CaretakerRole = Guid.CreateVersion7();
    private static readonly Guid OwnerRole = Guid.CreateVersion7();
    private static readonly Guid AdminRole = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<ITenantMembershipRepository> _members = new();
    private IReadOnlyCollection<string> _summedPermissions = [];

    private static TenantMemberRoleRecord Viewer => new(ViewerRole, "Viewer", RoleSeeds.Viewer, RoleSeeds.Permissions[RoleSeeds.Viewer]);

    private static TenantMemberRoleRecord Caretaker =>
        new(CaretakerRole, "Caretaker", RoleSeeds.Caretaker, RoleSeeds.Permissions[RoleSeeds.Caretaker]);

    private static TenantMemberRoleRecord Owner => new(OwnerRole, "Owner", RoleSeeds.Owner, RoleSeeds.Permissions[RoleSeeds.Owner]);

    private static TenantMemberRoleRecord Admin => new(AdminRole, "Admin", RoleSeeds.Admin, RoleSeeds.Permissions[RoleSeeds.Admin]);

    private static TenantMemberReasonRecord TeamReason => TeamGiving(Caretaker);

    private static TenantMemberReasonRecord TeamGiving(TenantMemberRoleRecord role) =>
        new(MembershipReasonKind.Team, new MemberReasonTeam(Guid.CreateVersion7(), "Clinic"), [role], null, false, null);

    private static TenantMemberReasonRecord Direct(IReadOnlyList<TenantMemberRoleRecord> roles, IReadOnlyList<string>? permissions = null) =>
        new(MembershipReasonKind.Direct, null, roles, permissions, false, null);

    private static TenantMemberRecord Member(
        Guid subject, IReadOnlyList<TenantMemberRoleRecord> roles, IReadOnlyList<TenantMemberReasonRecord> reasons,
        IReadOnlyList<string>? permissions = null) =>
        new(
            Id: Guid.CreateVersion7(), TenantId: TenantId, SubjectId: subject, SubjectName: "Staff",
            SubjectUsername: null, SubjectIsActive: true, IsSystemSubject: false, IsDemoSubject: false,
            IsPlatformAdmin: false, HasConfirmedEmail: true, Roles: roles, DirectPermissions: permissions, Label: null,
            Username: null, LimitTo24Hours: false, AccessEndsAt: null, LastUsedAt: null, CreatedFromInviteId: null,
            SysCreatedAt: Now, Reasons: reasons);

    private static IEnumerable<string> PermissionsOf(Guid role) =>
        role == ViewerRole ? Viewer.Permissions
        : role == CaretakerRole ? Caretaker.Permissions
        : role == OwnerRole ? Owner.Permissions
        : role == AdminRole ? Admin.Permissions
        : [];

    /// <summary>A member whose direct reason gives only a permission and whose team gives caretaker.</summary>
    private static TenantMemberRecord PermissionOnlyDirect(Guid subject) => Member(
        subject, [Caretaker], [Direct([], [Scope.GlucoseRead]), TeamReason], [Scope.GlucoseRead]);

    private static TenantMemberRecord TeamOnly(Guid subject) => Member(subject, [Caretaker], [TeamReason]);

    private static TenantMemberRecord Mixed(Guid subject) => Member(
        subject, [Viewer, Caretaker],
        [new TenantMemberReasonRecord(MembershipReasonKind.Direct, null, [Viewer], null, false, null), TeamReason]);

    private MemberInviteController Controller(Guid? caller = null)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(TenantId);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string> { Scope.FullAccess };
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = caller ?? Caller, TenantId = TenantId };
        var roles = new Mock<ITenantRoleService>();
        roles.Setup(r => r.ValidateRoleGrantAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<Guid> roleIds, IReadOnlyCollection<string> granter, CancellationToken _) =>
                roleIds.SelectMany(PermissionsOf).All(p => Scope.Satisfies(granter, p))
                    ? RoleGrantValidation.Valid
                    : new RoleGrantValidation(false, RoleGrantValidation.ExceedsGranter, "The role gives more than the granter holds."));
        roles.Setup(r => r.GetRolePermissionsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        roles.Setup(r => r.GetEffectivePermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [.. _summedPermissions]);
        return new MemberInviteController(
            Mock.Of<IMemberInviteService>(), _tenantService.Object, roles.Object, Mock.Of<ITenantMemberService>(),
            tenantAccessor.Object, _members.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private TenantMemberRecord Given(TenantMemberRecord member)
    {
        _members.Setup(m => m.GetMemberAsync(TenantId, member.Id, It.IsAny<CancellationToken>())).ReturnsAsync(member);
        _members.Setup(m => m.FindMemberAsync(TenantId, member.SubjectId, It.IsAny<CancellationToken>())).ReturnsAsync(member);
        return member;
    }

    private static void ShouldBeAccessFromTeam(IActionResult result)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should().Be(MemberAccessFromTeam.Code);
    }

    private void NothingWasWritten()
    {
        _tenantService.Verify(t => t.ReplaceMemberRolesAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _members.Verify(m => m.ReplaceRolesAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _members.Verify(m => m.SetDirectPermissionsAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _members.Verify(m => m.SetLimitTo24HoursAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenantService.Verify(t => t.SetMemberAccessEndAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Each_edit_of_a_member_with_no_direct_reason_is_refused_as_access_from_a_team()
    {
        var member = Given(TeamOnly(Guid.CreateVersion7()));
        var controller = Controller();

        ShouldBeAccessFromTeam(await controller.SetMemberRoles(
            member.Id, new SetMemberRolesRequest([ViewerRole]), TestPublicAccessCache.Create(), CancellationToken.None));
        ShouldBeAccessFromTeam(await controller.SetMemberPermissions(
            member.Id, new SetMemberPermissionsRequest([Scope.GlucoseRead]), TestPublicAccessCache.Create(), CancellationToken.None));
        ShouldBeAccessFromTeam(await controller.SetMemberLimitTo24Hours(
            member.Id, new SetMemberLimitTo24HoursRequest(true), TestPublicAccessCache.Create(), CancellationToken.None));
        ShouldBeAccessFromTeam(await controller.SetMemberAccessEnd(
            member.Id, new SetMemberAccessEndRequest(Now.AddDays(3)), new FakeTimeProvider(Now), CancellationToken.None));

        NothingWasWritten();
    }

    [Fact]
    public async Task Removing_a_member_with_no_direct_reason_is_refused_as_access_from_a_team()
    {
        var member = Given(TeamOnly(Guid.CreateVersion7()));
        _tenantService.Setup(t => t.RemoveMemberAsync(TenantId, member.SubjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemberRemovalResult(false, MemberAccessFromTeam.Detail) { AccessFromTeam = true });

        ShouldBeAccessFromTeam(await Controller().RemoveMember(member.SubjectId, CancellationToken.None));
    }

    [Fact]
    public async Task Stepping_down_with_no_direct_reason_is_refused_as_access_from_a_team()
    {
        Given(TeamOnly(Caller));
        _members.Setup(m => m.FindRoleIdBySlugAsync(TenantId, RoleSeeds.Caretaker, It.IsAny<CancellationToken>())).ReturnsAsync(CaretakerRole);

        ShouldBeAccessFromTeam(await Controller().StepDown(CancellationToken.None));

        NothingWasWritten();
    }

    [Fact]
    public async Task A_role_change_of_a_mixed_member_is_worked_out_from_the_direct_reasons_roles()
    {
        var member = Given(Mixed(Guid.CreateVersion7()));
        _tenantService.Setup(t => t.ReplaceMemberRolesAsync(
                TenantId, member.Id, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnerGuardedChange.Changed);

        var result = await Controller().SetMemberRoles(
            member.Id, new SetMemberRolesRequest([CaretakerRole]), TestPublicAccessCache.Create(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _tenantService.Verify(t => t.ReplaceMemberRolesAsync(
            TenantId, member.Id,
            It.Is<IReadOnlyCollection<Guid>>(removed => removed.SequenceEqual(new[] { ViewerRole })),
            It.Is<IReadOnlyCollection<Guid>>(added => added.SequenceEqual(new[] { CaretakerRole })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Taking_every_role_from_a_mixed_members_direct_reason_with_no_direct_permissions_is_refused_whatever_the_team_gives()
    {
        var member = Given(Mixed(Guid.CreateVersion7()));

        var result = await Controller().SetMemberRoles(
            member.Id, new SetMemberRolesRequest([]), TestPublicAccessCache.Create(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        NothingWasWritten();
    }

    [Fact]
    public async Task Clearing_a_mixed_members_direct_permissions_is_allowed_while_the_direct_reason_holds_a_role()
    {
        var member = Given(Mixed(Guid.CreateVersion7()));

        var result = await Controller().SetMemberPermissions(
            member.Id, new SetMemberPermissionsRequest(null), TestPublicAccessCache.Create(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _members.Verify(m => m.SetDirectPermissionsAsync(TenantId, member.Id, null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stepping_down_to_caretaker_is_refused_when_only_a_team_gives_it_and_nothing_is_written()
    {
        Given(Member(Caller, [Viewer, Caretaker], [Direct([Viewer]), TeamReason]));
        _members.Setup(m => m.FindRoleIdBySlugAsync(TenantId, RoleSeeds.Caretaker, It.IsAny<CancellationToken>())).ReturnsAsync(CaretakerRole);

        var result = await Controller().StepDown(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        NothingWasWritten();
    }

    [Fact]
    public async Task An_owner_let_in_directly_steps_down_to_caretaker_as_before()
    {
        var member = Given(Member(Caller, [Owner], []));
        _members.Setup(m => m.FindRoleIdBySlugAsync(TenantId, RoleSeeds.Caretaker, It.IsAny<CancellationToken>())).ReturnsAsync(CaretakerRole);
        _tenantService.Setup(t => t.ReplaceMemberRolesAsync(
                TenantId, member.Id, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnerGuardedChange.Changed);

        var result = await Controller().StepDown(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _tenantService.Verify(t => t.ReplaceMemberRolesAsync(
            TenantId, member.Id,
            It.Is<IReadOnlyCollection<Guid>>(removed => removed.SequenceEqual(new[] { OwnerRole })),
            It.Is<IReadOnlyCollection<Guid>>(added => added.SequenceEqual(new[] { CaretakerRole })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Giving_a_role_to_a_direct_reason_that_holds_only_a_permission_leaves_the_role_the_team_gives()
    {
        var member = Given(PermissionOnlyDirect(Guid.CreateVersion7()));
        _tenantService.Setup(t => t.ReplaceMemberRolesAsync(
                TenantId, member.Id, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnerGuardedChange.Changed);

        var result = await Controller().SetMemberRoles(
            member.Id, new SetMemberRolesRequest([ViewerRole]), TestPublicAccessCache.Create(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _tenantService.Verify(t => t.ReplaceMemberRolesAsync(
            TenantId, member.Id,
            It.Is<IReadOnlyCollection<Guid>>(removed => removed.Count == 0),
            It.Is<IReadOnlyCollection<Guid>>(added => added.SequenceEqual(new[] { ViewerRole })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clearing_the_permissions_of_a_direct_reason_with_no_role_is_refused_whatever_role_the_team_gives()
    {
        var member = Given(PermissionOnlyDirect(Guid.CreateVersion7()));

        var result = await Controller().SetMemberPermissions(
            member.Id, new SetMemberPermissionsRequest(null), TestPublicAccessCache.Create(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        NothingWasWritten();
    }

    [Fact]
    public async Task A_mixed_members_direct_reason_may_be_limited_although_the_team_gives_what_exempts_from_the_limit()
    {
        var member = Given(Member(Guid.CreateVersion7(), [Viewer, Admin], [Direct([Viewer]), TeamGiving(Admin)]));
        _summedPermissions = [.. Viewer.Permissions, .. Admin.Permissions];

        var result = await Controller().SetMemberLimitTo24Hours(
            member.Id, new SetMemberLimitTo24HoursRequest(true), TestPublicAccessCache.Create(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _members.Verify(m => m.SetLimitTo24HoursAsync(TenantId, member.Id, true, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_mixed_members_direct_reason_that_exempts_from_the_limit_cannot_be_limited()
    {
        var member = Given(Member(
            Guid.CreateVersion7(), [Caretaker], [Direct([], [Scope.TenantSettings]), TeamReason], [Scope.TenantSettings]));

        var result = await Controller().SetMemberLimitTo24Hours(
            member.Id, new SetMemberLimitTo24HoursRequest(true), TestPublicAccessCache.Create(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        NothingWasWritten();
    }
}
