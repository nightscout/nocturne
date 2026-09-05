using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// <c>GET /api/v4/me/tenants</c> and its overview both answer for the caller's own subject. A
/// credential that carries no subject is a member of nothing, which is an empty answer rather
/// than a rejected credential.
/// </summary>
[Trait("Category", "Unit")]
public class MyTenantsControllerTests
{
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<ITenantOverviewService> _overviewService = new();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    [Theory]
    [MemberData(nameof(SubjectlessCredentials))]
    public async Task A_subjectless_session_belongs_to_no_tenant_and_is_not_rejected(
        AuthType authType)
    {
        var controller = CreateController(Subjectless(authType));

        var result = await controller.GetMyTenants(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<TenantDto>>()
            .Which.Should().BeEmpty();
        _tenantService.Verify(
            s => s.GetTenantsForSubjectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [MemberData(nameof(SubjectlessCredentials))]
    public async Task A_subjectless_session_gets_an_empty_overview_and_is_not_rejected(
        AuthType authType)
    {
        var controller = CreateController(Subjectless(authType));

        var result = await controller.GetOverview(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TenantOverviewResponse>()
            .Which.Tenants.Should().BeEmpty();
        _overviewService.Verify(
            s => s.GetOverviewAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_member_gets_the_tenants_their_subject_belongs_to()
    {
        var tenant = new TenantDto(
            Guid.CreateVersion7(), "sleepy", "Sleepy", true, DateTime.UtcNow);
        _tenantService
            .Setup(s => s.GetTenantsForSubjectAsync(_subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tenant]);

        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _subjectId,
        });

        var result = await controller.GetMyTenants(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<TenantDto>>()
            .Which.Should().ContainSingle().Which.Should().Be(tenant);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected()
    {
        var controller = CreateController(AuthContext.Unauthenticated());

        var result = await controller.GetMyTenants(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// The credentials that authenticate without a subject of their own: a guest session, the
    /// instance key, and the development auto-authentication context, which mints
    /// <see cref="AuthType.ApiKey"/> with no subject.
    /// </summary>
    public static TheoryData<AuthType> SubjectlessCredentials =>
        [AuthType.Guest, AuthType.InstanceKey, AuthType.ApiKey];

    private static AuthContext Subjectless(AuthType authType) => new()
    {
        IsAuthenticated = true,
        AuthType = authType,
        SubjectId = null,
    };

    private MyTenantsController CreateController(AuthContext authContext)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = authContext;

        return new MyTenantsController(
            _tenantService.Object,
            _overviewService.Object,
            Options.Create(new OperatorConfiguration()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
