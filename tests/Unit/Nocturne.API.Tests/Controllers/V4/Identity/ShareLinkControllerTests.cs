using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Verifies the share-link endpoints are gated on the <c>sharing.manage</c> permission. The GET
/// gate is security-load-bearing because the returned URL embeds the share token.
/// </summary>
public sealed class ShareLinkControllerTests
{
    private readonly Mock<IShareLinkService> _service = new();
    private readonly Mock<IAuthAuditService> _audit = new();

    private ShareLinkController BuildController(params string[] grantedScopes) =>
        BuildController(historyClamped: false, grantedScopes);

    private ShareLinkController BuildController(bool historyClamped, params string[] grantedScopes) =>
        BuildController(historyClamped, accessEndsAt: null, grantedScopes);

    private static readonly DateTime CallerEnd = new(2026, 12, 1, 14, 0, 0, DateTimeKind.Utc);

    private ShareLinkController BuildController(DateTime accessEndsAt) =>
        BuildController(historyClamped: false, accessEndsAt, Scope.SharingManage, Scope.GlucoseRead);

    private ShareLinkController BuildController(bool historyClamped, DateTime? accessEndsAt, params string[] grantedScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(Guid.NewGuid());

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        if (historyClamped)
            httpContext.RequestServices = TestRequestServices.HistoryClamped();

        if (accessEndsAt is { } end)
        {
            var subjectId = Guid.NewGuid();
            var tenantId = Guid.NewGuid();
            httpContext.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = true,
                SubjectId = subjectId,
                TenantId = tenantId,
            };
            new RequestMembership(
                    subjectId,
                    tenantId,
                    new MembershipFacts(Guid.NewGuid(), new TenantMemberAccess(new HashSet<string>(grantedScopes), false, end), new TenantMemberAccess(new HashSet<string>(grantedScopes), false, end), null))
                .StoreOn(httpContext);
        }

        return new ShareLinkController(_service.Object, tenantAccessor.Object, _audit.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Theory]
    [InlineData(nameof(ShareLinkController.GetShareLink))]
    [InlineData(nameof(ShareLinkController.RevealShareLink))]
    [InlineData(nameof(ShareLinkController.RotateShareLink))]
    [InlineData(nameof(ShareLinkController.DisableShareLink))]
    [InlineData(nameof(ShareLinkController.SetShareLinkFullHistory))]
    [InlineData(nameof(ShareLinkController.SetShareLinkScopes))]
    public void Every_action_without_sharing_manage_is_forbidden_before_the_handler(string action)
    {
        ActionGate.Evaluate(
                typeof(ShareLinkController),
                action,
                new HashSet<string> { Scope.GlucoseRead },
                new AuthContext { IsAuthenticated = true, AuthType = AuthType.SessionCookie })
            .ShouldBeScopeRefusal(Scope.SharingManage);
    }

    private static void ShouldRefuseTheHistoryCeiling(IActionResult? result)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(
            "You can see the last 24 hours only, so you cannot give anyone more history than that.");
    }

    private void ShareHasFullHistory(bool fullHistory) =>
        _service.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, FullHistory = fullHistory });

    [Fact]
    public async Task SetShareLinkFullHistory_byAClampedCaller_isRefused()
    {
        var controller = BuildController(historyClamped: true, Scope.SharingManage);

        var result = await controller.SetShareLinkFullHistory(
            new SetShareFullHistoryRequest(true), CancellationToken.None);

        ShouldRefuseTheHistoryCeiling(result.Result);
        _service.Verify(s => s.SetFullHistoryAsync(
            It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetShareLinkFullHistory_byAClampedCaller_mayStillNarrowTheShare()
    {
        _service.Setup(s => s.SetFullHistoryAsync(It.IsAny<Guid>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, FullHistory = false });
        var controller = BuildController(historyClamped: true, Scope.SharingManage);

        var result = await controller.SetShareLinkFullHistory(
            new SetShareFullHistoryRequest(false), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RevealAndRotate_ofAFullHistoryShare_byAClampedCaller_areRefused()
    {
        ShareHasFullHistory(true);
        var controller = BuildController(historyClamped: true, Scope.SharingManage);

        ShouldRefuseTheHistoryCeiling((await controller.RevealShareLink(CancellationToken.None)).Result);
        ShouldRefuseTheHistoryCeiling((await controller.RotateShareLink(CancellationToken.None)).Result);
        _service.Verify(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(s => s.RotateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reveal_ofA24HourShare_byAClampedCaller_isAllowed()
    {
        ShareHasFullHistory(false);
        _service.Setup(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Url = null });
        var controller = BuildController(historyClamped: true, Scope.SharingManage);

        var result = await controller.RevealShareLink(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RevealShareLink_records_who_was_handed_the_link()
    {
        _service.Setup(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Url = "https://abc.share.nocturne.run" });
        var controller = BuildController(Scope.SharingManage);

        await controller.RevealShareLink(CancellationToken.None);

        // detailsJson is pinned to null, not It.IsAny: writing the URL into the audit row is
        // exactly the leak this endpoint exists to avoid, and an any-matcher cannot see it.
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.ShareLinkRevealed, It.IsAny<Guid?>(), true,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string?>(detailsJson => detailsJson == null),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task RevealShareLink_records_nothing_when_there_was_no_link_to_hand_over()
    {
        _service.Setup(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Url = null, CanReveal = false });
        var controller = BuildController(Scope.SharingManage);

        await controller.RevealShareLink(CancellationToken.None);

        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetShareLink_with_sharing_manage_returns_the_link_state()
    {
        _service.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = false });
        var controller = BuildController(Scope.SharingManage);

        var result = await controller.GetShareLink(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _service.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetShareLinkScopes_with_sharing_manage_applies_the_scopes()
    {
        _service.Setup(s => s.SetScopesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Scopes = [Scope.GlucoseRead] });
        var controller = BuildController(Scope.SharingManage, Scope.GlucoseRead);

        var result = await controller.SetShareLinkScopes(
            new SetShareScopesRequest([Scope.GlucoseRead]), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _service.Verify(s => s.SetScopesAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetShareLinkScopes_beyond_what_the_caller_can_read_is_refused()
    {
        var controller = BuildController(Scope.SharingManage, Scope.GlucoseRead);

        var result = await controller.SetShareLinkScopes(
            new SetShareScopesRequest([Scope.GlucoseRead, Scope.TreatmentsRead]), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _service.Verify(s => s.SetScopesAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetShareLinkScopes_returns_bad_request_for_invalid_scopes()
    {
        _service.Setup(s => s.SetScopesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid public share scopes: bogus.read"));
        var controller = BuildController(Scope.SharingManage);

        var result = await controller.SetShareLinkScopes(
            new SetShareScopesRequest(["bogus.read"]), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private static void ShouldRefuseTheEndDate(IActionResult? result)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(
            "Your access ends on a date, so you cannot change public sharing.");
    }

    private void ServiceAnswersEveryWrite()
    {
        var link = new ShareLinkDto { Enabled = true };
        _service.Setup(s => s.RotateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(link);
        _service.Setup(s => s.DisableAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(link);
        _service.Setup(s => s.SetFullHistoryAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(link);
        _service.Setup(s => s.SetScopesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(link);
    }

    public static TheoryData<string> ChangingActions => new()
    {
        nameof(ShareLinkController.RotateShareLink),
        nameof(ShareLinkController.DisableShareLink),
        nameof(ShareLinkController.SetShareLinkFullHistory),
        nameof(ShareLinkController.SetShareLinkScopes),
    };

    private static async Task<IActionResult?> ChangeAsync(ShareLinkController controller, string action) => action switch
    {
        nameof(ShareLinkController.RotateShareLink) => (await controller.RotateShareLink(CancellationToken.None)).Result,
        nameof(ShareLinkController.DisableShareLink) => (await controller.DisableShareLink(CancellationToken.None)).Result,
        nameof(ShareLinkController.SetShareLinkFullHistory) =>
            (await controller.SetShareLinkFullHistory(new SetShareFullHistoryRequest(false), CancellationToken.None)).Result,
        _ => (await controller.SetShareLinkScopes(new SetShareScopesRequest([Scope.GlucoseRead]), CancellationToken.None)).Result,
    };

    [Theory]
    [MemberData(nameof(ChangingActions))]
    public async Task Every_change_by_a_caller_whose_access_ends_is_refused_and_calls_no_service_method(string action)
    {
        ServiceAnswersEveryWrite();
        var controller = BuildController(CallerEnd);

        ShouldRefuseTheEndDate(await ChangeAsync(controller, action));

        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(ChangingActions))]
    public async Task Every_change_by_a_caller_with_no_end_is_allowed(string action)
    {
        ServiceAnswersEveryWrite();
        var controller = BuildController(Scope.SharingManage, Scope.GlucoseRead);

        (await ChangeAsync(controller, action)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Reading_and_revealing_still_answer_a_caller_whose_access_ends()
    {
        ShareHasFullHistory(false);
        _service.Setup(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Url = "https://abc.share.nocturne.run" });
        var controller = BuildController(CallerEnd);

        (await controller.GetShareLink(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await controller.RevealShareLink(CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    private ShareLinkController BuildMixedController(GrantCeiling? ceiling)
    {
        var controller = BuildController(Scope.SharingManage, Scope.GlucoseRead, Scope.TreatmentsRead);
        controller.HttpContext.SetGrantCeiling(ceiling);
        return controller;
    }

    [Fact]
    public async Task A_mixed_member_may_not_publish_a_category_only_the_team_gives()
    {
        var controller = BuildMixedController(new GrantCeiling(new HashSet<string> { Scope.SharingManage, Scope.GlucoseRead }, null, false));

        var result = await controller.SetShareLinkScopes(
            new SetShareScopesRequest([Scope.GlucoseRead, Scope.TreatmentsRead]), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_mixed_member_may_publish_what_the_direct_reason_reads()
    {
        ServiceAnswersEveryWrite();
        var controller = BuildMixedController(new GrantCeiling(new HashSet<string> { Scope.SharingManage, Scope.GlucoseRead }, null, false));

        (await controller.SetShareLinkScopes(new SetShareScopesRequest([Scope.GlucoseRead]), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_mixed_member_whose_direct_reason_is_clamped_may_not_widen_the_share_or_hand_out_a_full_history_link()
    {
        ShareHasFullHistory(true);
        var controller = BuildMixedController(new GrantCeiling(new HashSet<string> { Scope.SharingManage, Scope.GlucoseRead }, null, true));

        ShouldRefuseTheHistoryCeiling((await controller.SetShareLinkFullHistory(new SetShareFullHistoryRequest(true), CancellationToken.None)).Result);
        ShouldRefuseTheHistoryCeiling((await controller.RevealShareLink(CancellationToken.None)).Result);
        ShouldRefuseTheHistoryCeiling((await controller.RotateShareLink(CancellationToken.None)).Result);
        _service.Verify(s => s.SetFullHistoryAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(s => s.RotateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(ChangingActions))]
    public async Task Every_change_by_a_mixed_member_whose_direct_reason_ends_is_refused(string action)
    {
        ServiceAnswersEveryWrite();
        var controller = BuildMixedController(new GrantCeiling(new HashSet<string> { Scope.SharingManage, Scope.GlucoseRead }, CallerEnd, false));

        ShouldRefuseTheEndDate(await ChangeAsync(controller, action));
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(ChangingActions))]
    public async Task Every_change_by_a_team_only_member_is_refused(string action)
    {
        ServiceAnswersEveryWrite();
        var controller = BuildMixedController(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam((await ChangeAsync(controller, action))!);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_team_only_member_is_not_handed_the_link()
    {
        ShareHasFullHistory(false);
        var controller = BuildMixedController(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam((await controller.RevealShareLink(CancellationToken.None)).Result!);
        _service.Verify(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetShareLink_carries_the_callers_own_end()
    {
        ShareHasFullHistory(false);
        var dated = (await BuildController(CallerEnd).GetShareLink(CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ShareLinkDto>().Subject.CallerAccessEndsAt;

        ShareHasFullHistory(false);
        var open = (await BuildController(Scope.SharingManage).GetShareLink(CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ShareLinkDto>().Subject.CallerAccessEndsAt;

        dated.Should().Be(CallerEnd);
        open.Should().BeNull();
    }
}
