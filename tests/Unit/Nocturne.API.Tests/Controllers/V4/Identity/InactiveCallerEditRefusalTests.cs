using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// An edit that widens what an existing membership, role or share gives is refused to a caller whose
/// subject was deactivated but whose session is still live, before anything is read or written.
/// Every collaborator is a strict mock, so a call past the refusal fails the test.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InactiveCallerEditRefusalTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private readonly Mock<IMemberInviteService> _invites = new(MockBehavior.Strict);
    private readonly Mock<ITenantService> _tenants = new(MockBehavior.Strict);
    private readonly Mock<ITenantRoleService> _roles = new(MockBehavior.Strict);
    private readonly Mock<ITenantMemberService> _memberService = new(MockBehavior.Strict);
    private readonly Mock<ITenantMembershipRepository> _members = new(MockBehavior.Strict);
    private readonly Mock<IShareLinkService> _shareLinks = new(MockBehavior.Strict);
    private readonly Mock<ITenantAccessor> _accessor = new();

    public InactiveCallerEditRefusalTests() =>
        _accessor.SetupGet(a => a.TenantId).Returns(_tenantId);

    private ControllerContext InactiveCaller()
    {
        var http = new DefaultHttpContext();
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _subjectId, TenantId = _tenantId };
        new RequestMembership(_subjectId, _tenantId, null) { SubjectIsInactive = true }.StoreOn(http);
        return new ControllerContext { HttpContext = http };
    }

    private MemberInviteController Members() =>
        new(_invites.Object, _tenants.Object, _roles.Object, _memberService.Object, _accessor.Object, _members.Object)
        {
            ControllerContext = InactiveCaller(),
        };

    [Fact]
    public async Task A_member_edit_is_refused_and_nothing_is_read_or_written()
    {
        var controller = Members();
        var id = Guid.CreateVersion7();
        var cache = TestPublicAccessCache.Create();

        IActionResult[] results =
        [
            await controller.SetMemberRoles(id, new SetMemberRolesRequest([Guid.CreateVersion7()]), cache, default),
            await controller.SetMemberPermissions(id, new SetMemberPermissionsRequest([Scope.FullAccess]), cache, default),
            await controller.SetMemberLimitTo24Hours(id, new SetMemberLimitTo24HoursRequest(false), cache, default),
            await controller.SetMemberAccessEnd(id, new SetMemberAccessEndRequest(null), TimeProvider.System, default),
        ];

        results.Should().AllBeOfType<ChallengeResult>();
        _members.Invocations.Should().BeEmpty();
        _tenants.Invocations.Should().BeEmpty();
        _roles.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_edit_is_refused_and_nothing_is_read_or_written()
    {
        var controller = new RoleController(_roles.Object, _accessor.Object) { ControllerContext = InactiveCaller() };

        var result = await controller.UpdateRole(
            Guid.CreateVersion7(), new UpdateRoleRequest("Role", null, [Scope.FullAccess]), TestPublicAccessCache.Create(), default);

        result.Should().BeOfType<ChallengeResult>();
        _roles.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_share_edit_is_refused_and_nothing_is_written()
    {
        var controller = new ShareLinkController(_shareLinks.Object, _accessor.Object, Mock.Of<IAuthAuditService>())
        {
            ControllerContext = InactiveCaller(),
        };

        var fullHistory = await controller.SetShareLinkFullHistory(new SetShareFullHistoryRequest(true), default);
        var scopes = await controller.SetShareLinkScopes(new SetShareScopesRequest([Scope.GlucoseRead]), default);

        fullHistory.Result.Should().BeOfType<ChallengeResult>();
        scopes.Result.Should().BeOfType<ChallengeResult>();
        _shareLinks.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Revealing_the_share_link_is_refused_and_no_token_is_returned()
    {
        var controller = new ShareLinkController(_shareLinks.Object, _accessor.Object, Mock.Of<IAuthAuditService>())
        {
            ControllerContext = InactiveCaller(),
        };

        var result = await controller.RevealShareLink(default);

        result.Result.Should().BeOfType<ChallengeResult>();
        _shareLinks.Invocations.Should().BeEmpty();
    }
}
