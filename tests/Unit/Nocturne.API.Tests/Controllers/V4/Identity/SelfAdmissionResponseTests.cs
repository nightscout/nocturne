using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// How the refusals of a person letting themselves in, and of the platform administrator removing a
/// member who holds only a team's access, reach the client: each with its own problem code, while
/// the refusals a member let in directly meets answer as they did.
/// </summary>
public sealed class SelfAdmissionResponseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private DefaultHttpContext Http(IEnumerable<string> scopes)
    {
        var http = new DefaultHttpContext();
        http.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(scopes);
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _subjectId, TenantId = _tenantId };
        return http;
    }

    private ITenantAccessor Accessor()
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(_tenantId);
        return accessor.Object;
    }

    private static void ShouldBeCoded(IActionResult result, int status, string code)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should().Be(code);
    }

    private async Task<IActionResult> AcceptAsync(AcceptMemberInviteResult outcome)
    {
        var invites = new Mock<IMemberInviteService>();
        invites.Setup(i => i.AcceptInviteAsync("token", _subjectId, _tenantId)).ReturnsAsync(outcome);
        var controller = new MemberInviteController(
            invites.Object, Mock.Of<ITenantService>(), Mock.Of<ITenantRoleService>(), Mock.Of<ITenantMemberService>(),
            Accessor(), Mock.Of<ITenantMembershipRepository>())
        {
            ControllerContext = new ControllerContext { HttpContext = Http([]) },
        };
        return await controller.AcceptInvite("token");
    }

    private async Task<IActionResult> ApproveAsync(DecideMembershipRequestResult outcome)
    {
        var requests = new Mock<IMembershipRequestService>();
        requests
            .Setup(s => s.ApproveRequestAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<List<Guid>>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        var http = Http([Scope.MembersManage]);
        new RequestMembership(
                _subjectId,
                _tenantId,
                new MembershipFacts(Guid.CreateVersion7(), new TenantMemberAccess(new HashSet<string> { Scope.MembersManage }, false, null), new TenantMemberAccess(new HashSet<string> { Scope.MembersManage }, false, null), null))
            .StoreOn(http);
        var controller = new MembershipRequestController(requests.Object, Accessor())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return await controller.ApproveRequest(
            Guid.CreateVersion7(), new ApproveMembershipRequestRequest([Guid.CreateVersion7()], null), new FakeTimeProvider(Now),
            CancellationToken.None);
    }

    [Fact]
    public async Task Accepting_an_invite_you_made_is_forbidden_with_its_own_code()
    {
        var result = await AcceptAsync(new AcceptMemberInviteResult(false, SelfAdmission.OwnInviteCode, SelfAdmission.OwnInviteDetail));

        ShouldBeCoded(result, StatusCodes.Status403Forbidden, SelfAdmission.OwnInviteCode);
    }

    [Fact]
    public async Task Accepting_an_invite_as_a_member_let_in_directly_still_answers_400_with_no_code()
    {
        var result = await AcceptAsync(new AcceptMemberInviteResult(false, "already_member", "You are already a member of this tenant."));

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Extensions.Should().NotContainKey("code");
    }

    [Fact]
    public async Task Approving_your_own_request_is_forbidden_with_its_own_code()
    {
        var result = await ApproveAsync(new DecideMembershipRequestResult(false, SelfAdmission.OwnRequestDetail) { Code = SelfAdmission.OwnRequestCode });

        ShouldBeCoded(result, StatusCodes.Status403Forbidden, SelfAdmission.OwnRequestCode);
    }

    [Fact]
    public async Task Approving_a_request_from_a_member_let_in_directly_still_answers_400_with_the_result()
    {
        var refused = new DecideMembershipRequestResult(false, "The subject is already a member.");

        var result = await ApproveAsync(refused);

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().Be(refused);
    }

    [Fact]
    public async Task The_platform_administrator_removing_a_member_with_only_a_teams_access_gets_409_with_its_code()
    {
        var tenants = new Mock<ITenantService>();
        tenants.Setup(t => t.RemoveMemberAsync(_tenantId, _subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemberRemovalResult(false, MemberAccessFromTeam.Detail) { AccessFromTeam = true });
        var http = new DefaultHttpContext();
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, IsPlatformAdmin = true, AuthType = AuthType.InstanceKey };
        var controller = new TenantController(tenants.Object, Mock.Of<ITenantRoleService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.RemoveMember(_tenantId, _subjectId, CancellationToken.None);

        ShouldBeCoded(result, StatusCodes.Status409Conflict, MemberAccessFromTeam.Code);
    }
}
