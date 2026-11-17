using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

public class TenantCreationInviteControllerTests
{
    private readonly Mock<ITenantCreationInviteService> _invites = new();
    private readonly Mock<ITenantService> _tenants = new();

    private static ControllerContext Context(Guid? subjectId = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProblemDetailsFactory>(new TestProblemDetailsFactory());
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (subjectId is { } id)
            http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = id, SubjectName = "Sam" };
        return new ControllerContext { HttpContext = http };
    }

    private TenantCreationInviteController Public(Guid? subjectId = null) =>
        new(_invites.Object, _tenants.Object) { ControllerContext = Context(subjectId) };

    private static int? StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    [Theory]
    [InlineData(TenantInviteRefusalCode.NotFound, 404)]
    [InlineData(TenantInviteRefusalCode.Expired, 400)]
    [InlineData(TenantInviteRefusalCode.UsedUp, 400)]
    [InlineData(TenantInviteRefusalCode.SubdomainChangeUnconfirmed, 409)]
    [InlineData(TenantInviteRefusalCode.BaseDomainRequired, 409)]
    public async Task A_refused_link_answers_with_its_status_and_reason(TenantInviteRefusalCode code, int status)
    {
        _invites.Setup(i => i.GetInfoAsync("t", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteInfo>.Refused(code, "Because."));

        var result = await Public().GetInfo("t", default);

        StatusOf(result).Should().Be(status);
        ((ObjectResult)result).Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be("Because.");
    }

    [Fact]
    public async Task A_usable_link_reports_who_is_signed_in()
    {
        var info = new TenantCreationInviteInfo("example.org", "Rhys", DateTime.UtcNow.AddDays(1));
        _invites.Setup(i => i.GetInfoAsync("t", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteInfo>.Ok(info));

        var result = await Public(Guid.CreateVersion7()).GetInfo("t", default);

        var view = (TenantCreationInviteViewInfo)((OkObjectResult)result).Value!;
        view.SignedIn.Should().BeTrue();
        view.ViewerName.Should().Be("Sam");
    }

    [Fact]
    public async Task The_slug_check_answers_only_for_a_usable_link()
    {
        _invites.Setup(i => i.GetInfoAsync("bad", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteInfo>.Refused(TenantInviteRefusalCode.NotFound, "No."));

        var result = await Public().ValidateSlug("bad", "sam", default);

        StatusOf(result).Should().Be(404);
        _tenants.Verify(t => t.ValidateSlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redeem_refuses_a_bad_slug_before_it_takes_a_use()
    {
        _tenants.Setup(t => t.ValidateSlugAsync("Bad!", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(false, "Slug must be 3-63 characters"));

        var result = await Public(Guid.CreateVersion7()).Redeem("t", new RedeemTenantCreationInviteRequest("Bad!", "Sam"), default);

        StatusOf(result).Should().Be(400);
        _invites.Verify(i => i.ClaimAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redeem_creates_the_tenant_for_the_signed_in_caller()
    {
        var subject = Guid.CreateVersion7();
        var inviteId = Guid.CreateVersion7();
        _tenants.Setup(t => t.ValidateSlugAsync("sam", It.IsAny<CancellationToken>())).ReturnsAsync(new SlugValidationResult(true));
        _invites.Setup(i => i.ClaimAsync("t", It.IsAny<CancellationToken>())).ReturnsAsync(TenantInviteOutcome<Guid>.Ok(inviteId));
        _invites.Setup(i => i.CreateTenantAsync(inviteId, "sam", "Sam", subject, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteRedeemed>.Ok(
                new TenantCreationInviteRedeemed(Guid.CreateVersion7(), "sam", "Sam", "https://sam.example.org")));

        var result = await Public(subject).Redeem("t", new RedeemTenantCreationInviteRequest("sam", "Sam"), default);

        ((TenantCreationInviteRedeemed)((OkObjectResult)result).Value!).Address.Should().Be("https://sam.example.org");
    }

    [Fact]
    public async Task The_admin_create_and_revoke_answer_with_the_service_outcome()
    {
        var admin = new TenantCreationInviteAdminController(_invites.Object) { ControllerContext = Context(Guid.CreateVersion7()) };
        _invites.Setup(i => i.CreateAsync(It.IsAny<Guid>(), It.IsAny<CreateTenantCreationInviteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteCreated>.Refused(
                TenantInviteRefusalCode.SubdomainChangeUnconfirmed, "Confirm first."));
        _invites.Setup(i => i.RevokeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        StatusOf(await admin.Create(new CreateTenantCreationInviteRequest(null, 1, 7), default)).Should().Be(409);
        (await admin.Revoke(Guid.CreateVersion7(), default)).Should().BeOfType<NotFoundResult>();
    }

    private sealed class TestProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext, int? statusCode = null, string? title = null, string? type = null,
            string? detail = null, string? instance = null) =>
            new() { Status = statusCode, Title = title, Detail = detail };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext, Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelStateDictionary,
            int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode };
    }
}
