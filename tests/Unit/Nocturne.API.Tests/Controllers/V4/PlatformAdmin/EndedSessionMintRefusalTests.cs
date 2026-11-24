using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.PlatformAdmin;

/// <summary>
/// A platform admin's action that mints a credential is minted only for a caller whose session is
/// live: an admin whose session ended is answered as a caller with no session is, and nothing is minted.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EndedSessionMintRefusalTests
{
    private static readonly Guid AdminId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static ControllerContext EndedSession(bool tenantResolved = false)
    {
        var subjects = new Mock<ISubjectRepository>();
        subjects.Setup(s => s.GetSignInStateAsync(AdminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectSignInState(IsActive: true, IsPlatformAdmin: true));
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(subjects.Object)
                .AddLiveSession()
                .BuildServiceProvider(),
        };
        http.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = AdminId,
            TenantId = tenantResolved ? TenantId : null,
            SessionId = "ended-session",
            IsPlatformAdmin = true,
        };
        return new ControllerContext { HttpContext = http };
    }

    [Fact]
    public async Task A_tenant_direct_grant_is_not_minted_from_an_ended_session()
    {
        var grants = new Mock<IDirectGrantService>(MockBehavior.Strict);
        var controller = new TenantDirectGrantController(
            Mock.Of<ITenantMemberService>(MockBehavior.Strict), grants.Object, Mock.Of<ISubjectRepository>(MockBehavior.Strict))
        {
            ControllerContext = EndedSession(),
        };

        var result = await controller.Create(
            TenantId,
            new AdminCreateDirectGrantRequest { SubjectId = Guid.CreateVersion7(), Label = "x", Scopes = ["glucose.read"] },
            CancellationToken.None);

        result.Result.Should().BeOfType<ChallengeResult>();
        grants.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_creation_invite_is_not_minted_from_an_ended_session()
    {
        var invites = new Mock<ITenantCreationInviteService>(MockBehavior.Strict);
        var controller = new TenantCreationInviteAdminController(invites.Object) { ControllerContext = EndedSession() };

        (await controller.Create(new CreateTenantCreationInviteRequest(null, 1, 7), CancellationToken.None))
            .Should().BeOfType<ChallengeResult>();
        invites.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task No_member_login_code_member_or_tenant_is_minted_from_an_ended_session()
    {
        var tenants = new Mock<ITenantService>(MockBehavior.Strict);
        var controller = new TenantController(tenants.Object, Mock.Of<ITenantRoleService>(MockBehavior.Strict))
        {
            ControllerContext = EndedSession(),
        };

        (await controller.IssueLoginCode(
                TenantId, Guid.CreateVersion7(),
                Mock.Of<INocturneDbContextFactory>(MockBehavior.Strict),
                Mock.Of<ILoginCodeService>(MockBehavior.Strict),
                Mock.Of<ISubjectRepository>(MockBehavior.Strict),
                Mock.Of<ITenantMemberService>(MockBehavior.Strict),
                CancellationToken.None))
            .Should().BeOfType<ChallengeResult>();
        (await controller.AddMember(TenantId, new AddMemberRequest(Guid.CreateVersion7(), []), CancellationToken.None))
            .Should().BeOfType<ChallengeResult>();
        (await controller.Provision(new ProvisionRequest("slug", "Slug", "owner", "owner@example.org"), CancellationToken.None))
            .Should().BeOfType<ChallengeResult>();
        tenants.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_access_request_is_not_approved_from_an_ended_session()
    {
        var requests = new Mock<IAccessRequestService>(MockBehavior.Strict);
        var controller = new AccessRequestController(
            Mock.Of<ITenantMembershipRepository>(MockBehavior.Strict),
            Mock.Of<ISubjectRepository>(MockBehavior.Strict),
            Mock.Of<ISubjectDeletion>(MockBehavior.Strict),
            requests.Object,
            Mock.Of<ITenantAccessor>(),
            Mock.Of<IInAppNotificationService>(MockBehavior.Strict),
            NullLogger<AccessRequestController>.Instance)
        {
            ControllerContext = EndedSession(tenantResolved: true),
        };

        (await controller.Approve(Guid.CreateVersion7(), new ApproveAccessRequestRequest(), CancellationToken.None))
            .Should().BeOfType<ChallengeResult>();
        requests.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Platform_access_sends_an_ended_session_to_sign_in_and_mints_nothing()
    {
        var jwt = new Mock<IJwtService>(MockBehavior.Strict);
        var controller = new PlatformAccessController(
            Mock.Of<ITenantRepository>(MockBehavior.Strict),
            jwt.Object,
            Mock.Of<IAuthAuditService>(MockBehavior.Strict),
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions()),
            NullLogger<PlatformAccessController>.Instance)
        {
            ControllerContext = EndedSession(),
        };

        var result = await controller.Access("acme", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url
            .Should().Be("/api/auth/oidc/login?returnUrl=%2Fapi%2Fauth%2Fplatform-access%3Ftenant%3Dacme");
        jwt.Invocations.Should().BeEmpty();
    }
}
