using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The end an approved membership carries: the approver's own end when the approval names none,
/// never later than it, and never at or before now.
/// </summary>
public sealed class MembershipRequestAccessEndTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _approverId = Guid.CreateVersion7();
    private readonly Mock<IMembershipRequestService> _service = new();

    private async Task<(IActionResult Result, DateTime? Passed)> ApproveAsync(DateTime? approverAccessEndsAt, DateTimeOffset? requested)
    {
        DateTime? passed = null;
        _service
            .Setup(s => s.ApproveRequestAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<List<Guid>>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(i => passed = (DateTime?)i.Arguments[5]))
            .ReturnsAsync(new DecideMembershipRequestResult(true, null));
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(_tenantId);

        var http = new DefaultHttpContext();
        http.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string> { Scope.MembersManage };
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _approverId, TenantId = _tenantId };
        new RequestMembership(
                _approverId,
                _tenantId,
                new MembershipFacts(Guid.CreateVersion7(), new TenantMemberAccess(new HashSet<string> { Scope.MembersManage }, false, approverAccessEndsAt), new TenantMemberAccess(new HashSet<string> { Scope.MembersManage }, false, approverAccessEndsAt), null))
            .StoreOn(http);
        var controller = new MembershipRequestController(_service.Object, accessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.ApproveRequest(
            Guid.CreateVersion7(), new ApproveMembershipRequestRequest([Guid.CreateVersion7()], requested), new FakeTimeProvider(Now),
            CancellationToken.None);
        return (result, passed);
    }

    private static void ShouldRefuse(IActionResult result, int status, string detail)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task An_approval_that_names_no_end_inherits_the_approvers_end()
    {
        var (_, passed) = await ApproveAsync(Now.UtcDateTime.AddDays(5), requested: null);

        passed.Should().Be(Now.UtcDateTime.AddDays(5));
    }

    [Fact]
    public async Task An_end_later_than_the_approvers_own_is_refused()
    {
        var (result, _) = await ApproveAsync(Now.UtcDateTime.AddDays(5), Now.AddDays(6));

        ShouldRefuse(result, StatusCodes.Status403Forbidden, HttpContextExtensions.AccessEndCeilingDetail);
        _service.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_approver_with_no_end_may_name_any_future_end_or_none()
    {
        (await ApproveAsync(approverAccessEndsAt: null, Now.AddYears(2))).Passed.Should().Be(Now.UtcDateTime.AddYears(2));
        (await ApproveAsync(approverAccessEndsAt: null, requested: null)).Passed.Should().BeNull();
    }

    [Fact]
    public async Task An_end_at_or_before_now_is_refused()
    {
        var (result, _) = await ApproveAsync(approverAccessEndsAt: null, Now);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AccessEndRequest.PastDetail);
        _service.Invocations.Should().BeEmpty();
    }
    private async Task<(IActionResult Result, IReadOnlyCollection<string>? Scopes, DateTime? End, bool? Clamped)> ApproveAsMixedMemberAsync(GrantCeiling? ceiling)
    {
        (IReadOnlyCollection<string>? Scopes, DateTime? End, bool? Clamped) passed = default;
        _service
            .Setup(s => s.ApproveRequestAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<List<Guid>>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(i => passed = (
                (IReadOnlyCollection<string>)i.Arguments[4], (DateTime?)i.Arguments[5], (bool)i.Arguments[6])))
            .ReturnsAsync(new DecideMembershipRequestResult(true, null));
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(_tenantId);
        var sum = new HashSet<string> { Scope.MembersManage, Scope.GlucoseRead, Scope.TreatmentsRead };

        var http = new DefaultHttpContext();
        http.Items["GrantedScopes"] = (IReadOnlySet<string>)sum;
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _approverId, TenantId = _tenantId };
        new RequestMembership(
                _approverId, _tenantId,
                new MembershipFacts(Guid.CreateVersion7(), new TenantMemberAccess(sum, false, null), ceiling is null ? null : new TenantMemberAccess(ceiling.Scopes, ceiling.HistoryClamped, ceiling.AccessEndsAt), null))
            .StoreOn(http);
        http.SetGrantCeiling(ceiling);
        var controller = new MembershipRequestController(_service.Object, accessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.ApproveRequest(
            Guid.CreateVersion7(), new ApproveMembershipRequestRequest([Guid.CreateVersion7()]), new FakeTimeProvider(Now), CancellationToken.None);
        return (result, passed.Scopes, passed.End, passed.Clamped);
    }

    [Fact]
    public async Task A_mixed_member_approves_within_the_direct_reason_alone()
    {
        var directEnd = Now.UtcDateTime.AddDays(5);
        var ceiling = new GrantCeiling(new HashSet<string> { Scope.MembersManage, Scope.GlucoseRead }, directEnd, true);

        var (_, scopes, end, clamped) = await ApproveAsMixedMemberAsync(ceiling);

        scopes.Should().BeEquivalentTo(ceiling.Scopes);
        end.Should().Be(directEnd);
        clamped.Should().BeTrue();
    }

    [Fact]
    public async Task A_team_only_member_approves_nothing()
    {
        var (result, _, _, _) = await ApproveAsMixedMemberAsync(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(result);
        _service.Invocations.Should().BeEmpty();
    }
}
