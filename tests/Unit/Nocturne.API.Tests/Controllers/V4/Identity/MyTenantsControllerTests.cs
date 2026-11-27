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
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
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

        var result = await controller.GetMyTenants(ct: CancellationToken.None);

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

        var result = await controller.GetOverview(null, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TenantOverviewResponse>()
            .Which.Tenants.Should().BeEmpty();
        _overviewService.Verify(
            s => s.GetOverviewAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(90)]
    public async Task A_review_is_read_for_a_supported_period(int days)
    {
        var review = new TenantReviewResponse(default, default, default, default, []);
        _overviewService
            .Setup(s => s.GetReviewAsync(
                _subjectId, It.IsAny<IReadOnlySet<string>>(), AuthType.SessionCookie, days,
                false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        var result = await CreateController(Member()).GetReview(null, days, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(review);
    }

    [Theory]
    [InlineData(-14)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(91)]
    [InlineData(365)]
    public async Task A_review_of_any_other_period_is_refused_and_nothing_is_read(int days)
    {
        var result = await CreateController(Member()).GetReview(null, days, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _overviewService.Verify(
            s => s.GetReviewAsync(
                It.IsAny<Guid?>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [MemberData(nameof(SubjectlessCredentials))]
    public async Task A_subjectless_session_is_answered_by_the_service_with_no_subject(AuthType authType)
    {
        var review = new TenantReviewResponse(default, default, default, default, []);
        _overviewService
            .Setup(s => s.GetReviewAsync(
                null, It.IsAny<IReadOnlySet<string>>(), authType, 14,
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        var result = await CreateController(Subjectless(authType)).GetReview(null, 14, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(review);
    }

    [Fact]
    public async Task A_group_asked_for_is_read_as_that_group_and_everyone_is_not_read()
    {
        var group = Guid.CreateVersion7();
        var overview = new TenantOverviewResponse([], 2);
        _overviewService
            .Setup(s => s.GetGroupOverviewAsync(
                _subjectId, group, It.IsAny<IReadOnlySet<string>>(), AuthType.SessionCookie, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(overview);

        var result = await CreateController(Member()).GetOverview(group, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(overview);
        _overviewService.Verify(
            s => s.GetOverviewAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_group_the_caller_is_not_in_is_not_found()
    {
        var result = await CreateController(Member()).GetOverview(Guid.CreateVersion7(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [MemberData(nameof(SubjectlessCredentials))]
    public async Task A_subjectless_session_has_no_groups_and_asking_for_one_is_not_found(AuthType authType)
    {
        var controller = CreateController(Subjectless(authType));

        (await controller.GetGroups(CancellationToken.None)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<StaffGroup>>().Which.Should().BeEmpty();
        (await controller.GetOverview(Guid.CreateVersion7(), CancellationToken.None)).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        (await controller.GetReview(Guid.CreateVersion7(), 14, CancellationToken.None)).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _overviewService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_members_groups_are_the_services()
    {
        IReadOnlyList<StaffGroup> groups = [new(Guid.CreateVersion7(), "Birch cabin", "Camp", false)];
        _overviewService.Setup(s => s.GetGroupsAsync(_subjectId, It.IsAny<CancellationToken>())).ReturnsAsync(groups);

        (await CreateController(Member()).GetGroups(CancellationToken.None)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(groups);
    }

    [Fact]
    public async Task A_review_of_a_group_is_read_for_that_group_and_one_the_caller_is_not_in_is_not_found()
    {
        var group = Guid.CreateVersion7();
        var review = new TenantReviewResponse(default, default, default, default, []);
        _overviewService
            .Setup(s => s.GetGroupReviewAsync(
                _subjectId, group, It.IsAny<IReadOnlySet<string>>(), AuthType.SessionCookie, 30, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        (await CreateController(Member()).GetReview(group, 30, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(review);
        (await CreateController(Member()).GetReview(Guid.CreateVersion7(), 30, CancellationToken.None)).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _overviewService.Verify(
            s => s.GetReviewAsync(
                It.IsAny<Guid?>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_person_is_read_for_the_named_tenant()
    {
        var tenantId = Guid.NewGuid();
        var detail = new TenantPersonDetail(
            new TenantOverviewItem(
                tenantId, "family", "Family", null, null, GlucoseStatus.Unknown,
                new TenantOverviewThresholds(55, 80, 180, 260), null, null, [], null, null, null, []),
            [], null, default, default);
        _overviewService
            .Setup(s => s.GetPersonAsync(
                _subjectId, tenantId, It.IsAny<IReadOnlySet<string>>(), AuthType.SessionCookie,
                false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(detail);

        var result = await CreateController(Member()).GetPerson(tenantId, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(detail);
    }

    [Fact]
    public async Task A_person_is_refused_to_an_unauthenticated_caller_and_nothing_is_read()
    {
        var result = await CreateController(AuthContext.Unauthenticated())
            .GetPerson(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
        VerifyNoPersonRead();
    }

    [Fact]
    public async Task A_person_with_no_tenant_id_is_a_bad_request_and_nothing_is_read()
    {
        var result = await CreateController(Member()).GetPerson(Guid.Empty, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        VerifyNoPersonRead();
    }

    [Fact]
    public async Task A_person_the_service_does_not_return_is_not_found()
    {
        var result = await CreateController(Member()).GetPerson(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [MemberData(nameof(SubjectlessCredentials))]
    public async Task A_subjectless_session_is_answered_by_the_service_with_no_subject_for_a_person(
        AuthType authType)
    {
        var tenantId = Guid.NewGuid();

        var result = await CreateController(Subjectless(authType)).GetPerson(tenantId, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _overviewService.Verify(
            s => s.GetPersonAsync(
                null, tenantId, It.IsAny<IReadOnlySet<string>>(), authType,
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void VerifyNoPersonRead() =>
        _overviewService.Verify(
            s => s.GetPersonAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Theory]
    [MemberData(nameof(SubjectlessCredentials))]
    public async Task A_subjectless_session_is_refused_an_unsupported_period(AuthType authType)
    {
        var result = await CreateController(Subjectless(authType)).GetReview(null, 15, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
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

        var result = await controller.GetMyTenants(ct: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<TenantDto>>()
            .Which.Should().ContainSingle().Which.Should().Be(tenant);
    }

    [Fact]
    public async Task Asking_for_reachable_tenants_returns_only_those_the_member_can_go_to()
    {
        var reachable = new TenantDto(
            Guid.CreateVersion7(), "sleepy", "Sleepy", true, DateTime.UtcNow);
        _tenantService
            .Setup(s => s.GetReachableTenantsForSubjectAsync(_subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([reachable]);

        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _subjectId,
        });

        var result = await controller.GetMyTenants(reachableOnly: true, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<TenantDto>>()
            .Which.Should().ContainSingle().Which.Should().Be(reachable);
        _tenantService.Verify(
            s => s.GetTenantsForSubjectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected()
    {
        var controller = CreateController(AuthContext.Unauthenticated());

        var result = await controller.GetMyTenants(ct: CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// The credentials that authenticate without a subject of their own: a guest session, the
    /// instance key, and the development auto-authentication context, which mints
    /// <see cref="AuthType.ApiKey"/> with no subject.
    /// </summary>
    public static TheoryData<AuthType> SubjectlessCredentials =>
        [AuthType.Guest, AuthType.InstanceKey, AuthType.ApiKey];

    private AuthContext Member() => new()
    {
        IsAuthenticated = true,
        AuthType = AuthType.SessionCookie,
        SubjectId = _subjectId,
    };

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
