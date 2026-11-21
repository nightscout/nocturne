using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The bridge joins a browser to one overview room per tenant this answer names, so it names
/// exactly the tenants the overview hub would join and refuses every credential the hub refuses.
/// </summary>
[Trait("Category", "Unit")]
public class OverviewAdmissionControllerTests
{
    private static readonly Guid Subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static GlucoseReadTenant Tenant(Guid id) =>
        new(
            new TenantRecord(Id: id, Slug: id.ToString("N")[..8], DisplayName: "T", IsActive: true, IsDemo: false, AllowAccessRequests: true, AllowPublicDocs: false,
                OnboardingCompletedAt: null, SetupHubEnrolledAt: null, SetupStripDismissedRevision: null, PatientRelationship: null, HandedOverAt: null,
                DefaultGlucoseUnits: null, LastReadingAt: null, ShareTokenHash: null, ShareTokenEncrypted: null, ShareTokenSetAt: null,
                ShareLastAccessedAt: null, SysCreatedAt: DateTime.UtcNow, SysUpdatedAt: DateTime.UtcNow, DemoConfig: null),
            new HashSet<string> { Scope.GlucoseRead },
            MembershipLimitTo24Hours: false,
            new HashSet<string> { Scope.GlucoseRead });

    private static (OverviewAdmissionController Controller, Mock<ITenantOverviewService> Overview) Create(
        AuthContext? caller)
    {
        var overview = new Mock<ITenantOverviewService>();
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(
                Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Tenant(TenantA), Tenant(TenantB)]);

        var httpContext = new DefaultHttpContext();
        if (caller is not null) httpContext.SetAuthContext(caller);
        httpContext.SetGrantedScopes(Scope.Normalize([Scope.GlucoseRead]));

        var controller = new OverviewAdmissionController(overview.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (controller, overview);
    }

    private static AuthContext Caller(AuthType authType, Guid? subjectId) => new()
    {
        IsAuthenticated = true,
        AuthType = authType,
        SubjectId = subjectId,
    };

    public static TheoryData<AuthType> TenantBoundCredentials()
    {
        var data = new TheoryData<AuthType>();
        foreach (var type in Enum.GetValues<AuthType>())
        {
            if (!MemberScopeResolver.UnscopedCredentialTypes.Contains(type)) data.Add(type);
        }
        return data;
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.OidcToken)]
    [InlineData(AuthType.LegacyJwt)]
    public async Task A_subjects_own_login_is_admitted_to_exactly_its_glucose_readable_tenants(AuthType authType)
    {
        var (controller, overview) = Create(Caller(authType, Subject));

        var result = await controller.GetOverviewAdmission(CancellationToken.None);

        var admission = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<OverviewAdmission>().Subject;
        admission.SubjectId.Should().Be(Subject);
        admission.TenantIds.Should().Equal(TenantA, TenantB);
        overview.Verify(o => o.GetGlucoseReadTenantsAsync(
            Subject, It.IsAny<IReadOnlySet<string>>(), authType, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [MemberData(nameof(TenantBoundCredentials))]
    public async Task A_credential_issued_for_one_tenant_is_refused(AuthType authType)
    {
        var (controller, overview) = Create(Caller(authType, Subject));

        var result = await controller.GetOverviewAdmission(CancellationToken.None);

        result.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        overview.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_login_with_no_subject_of_its_own_is_refused()
    {
        var (controller, overview) = Create(Caller(AuthType.SessionCookie, subjectId: null));

        var result = await controller.GetOverviewAdmission(CancellationToken.None);

        result.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        overview.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        var (controller, overview) = Create(caller: null);

        var result = await controller.GetOverviewAdmission(CancellationToken.None);

        result.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        overview.VerifyNoOtherCalls();
    }
}
