using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Services.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// The session carries the tenant's default glucose units beside the member's own preferences,
/// so a member who has chosen none reads the default and an inherited default is never taken for
/// a saved choice. A session caller that a mint would refuse reads as signed out.
/// </summary>
[Trait("Category", "Unit")]
public class OidcControllerSessionTests
{
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly Mock<ITenantService> _tenants = new(MockBehavior.Strict);

    [Fact]
    public async Task GetSession_CarriesTheTenantDefaultApartFromTheMembersOwnPreferences()
    {
        var own = new UserDisplayPreferences { TimeFormat = "24" };
        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mmol");

        var session = await GetSession(own, TenantId);

        session.DefaultGlucoseUnits.Should().Be("mmol");
        session.Preferences.Should().BeSameAs(own);
        session.Preferences!.GlucoseUnits.Should().BeNull();
    }

    [Fact]
    public async Task GetSession_ReadsNoTenantDefaultOutsideATenant()
    {
        var session = await GetSession(new UserDisplayPreferences(), tenantId: null);

        session.DefaultGlucoseUnits.Should().BeNull();
        _tenants.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.LegacyJwt)]
    public async Task GetSession_ReadsASessionCallerWhoseSessionHasEndedAsSignedOut(AuthType authType)
    {
        var session = await GetSession(
            new UserDisplayPreferences(), TenantId,
            auth => { auth.AuthType = authType; auth.SessionId = "ended-session"; },
            services => services.AddSingleton(SessionsFindingLive(false)));

        session.IsAuthenticated.Should().BeFalse();
        session.SubjectId.Should().BeNull();
        _tenants.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSession_ReadsTheLiveSessionOfAnInactiveSubjectAsSignedOutOnATenantRoute()
    {
        var session = await GetSession(
            new UserDisplayPreferences(), TenantId,
            auth => { auth.AuthType = AuthType.SessionCookie; auth.SessionId = LiveSession.Id; },
            services => services.AddLiveSession(),
            http => new RequestMembership(SubjectId, TenantId, null) { SubjectIsInactive = true }.StoreOn(http));

        session.IsAuthenticated.Should().BeFalse();
        _tenants.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSession_ReadsTheLiveSessionOfAnInactiveSubjectAsSignedOutWithoutATenant()
    {
        var session = await GetSession(
            new UserDisplayPreferences(), tenantId: null,
            auth => { auth.AuthType = AuthType.SessionCookie; auth.SessionId = LiveSession.Id; },
            services => services.AddLiveSession().AddSingleton(SubjectsFindingActive(false)));

        session.IsAuthenticated.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetSession_ReadsTheLiveSessionOfAnActiveSubjectAsSignedIn(bool onATenantRoute)
    {
        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mmol");

        var session = await GetSession(
            new UserDisplayPreferences(), onATenantRoute ? TenantId : null,
            auth => { auth.AuthType = AuthType.SessionCookie; auth.SessionId = LiveSession.Id; },
            services => services.AddLiveSession().AddActiveSubjects(),
            http => new RequestMembership(SubjectId, TenantId, null).StoreOn(http));

        session.IsAuthenticated.Should().BeTrue();
        session.SubjectId.Should().Be(SubjectId);
    }

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.OAuthAccessToken)]
    public async Task GetSession_LeavesACallerThatIsNotASessionAsItWasEvenWhenItsSubjectIsInactive(AuthType authType)
    {
        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mmol");
        var sessions = new Mock<IFirstPartyTokenRepository>(MockBehavior.Strict);
        var subjects = new Mock<ISubjectRepository>(MockBehavior.Strict);

        var session = await GetSession(
            new UserDisplayPreferences(), TenantId,
            auth => auth.AuthType = authType,
            services => services
                .AddSingleton(new CallerSessionLiveness(sessions.Object, TimeProvider.System))
                .AddSingleton(subjects.Object),
            http => new RequestMembership(SubjectId, TenantId, null) { SubjectIsInactive = true }.StoreOn(http));

        session.IsAuthenticated.Should().BeTrue();
        session.SubjectId.Should().Be(SubjectId);
        sessions.VerifyNoOtherCalls();
        subjects.VerifyNoOtherCalls();
    }

    private static CallerSessionLiveness SessionsFindingLive(bool live)
    {
        var sessions = new Mock<IFirstPartyTokenRepository>();
        sessions
            .Setup(s => s.HasLiveSessionAsync(SubjectId, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(live);
        return new CallerSessionLiveness(sessions.Object, TimeProvider.System);
    }

    private static ISubjectRepository SubjectsFindingActive(bool active)
    {
        var subjects = new Mock<ISubjectRepository>();
        subjects.Setup(s => s.GetSignInStateAsync(SubjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectSignInState(IsActive: active, IsPlatformAdmin: false));
        return subjects.Object;
    }

    private async Task<SessionInfo> GetSession(
        UserDisplayPreferences own,
        Guid? tenantId,
        Action<AuthContext>? caller = null,
        Action<IServiceCollection>? services = null,
        Action<HttpContext>? request = null)
    {
        _authService.Setup(a => a.GetUserInfoAsync(SubjectId))
            .ReturnsAsync(new OidcUserInfo { SubjectId = SubjectId, Preferences = own });
        _tenantMemberService.Setup(t => t.GetMemberRoleNamesAsync(SubjectId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var requestServices = new ServiceCollection();
        services?.Invoke(requestServices);

        var controller = new OidcController(
            _authService.Object,
            new Mock<IOidcProviderService>().Object,
            new Mock<ISubjectService>().Object,
            new Mock<IAuthAuditService>().Object,
            _tenantMemberService.Object,
            new RecoverySession(new Mock<INocturneDbContextFactory>().Object, new Mock<ILoginCodeService>().Object, new Mock<ITenantMemberService>().Object, Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = requestServices.BuildServiceProvider() },
            },
        };
        var auth = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = SubjectId,
            TenantId = tenantId,
        };
        caller?.Invoke(auth);
        controller.HttpContext.Items["AuthContext"] = auth;
        request?.Invoke(controller.HttpContext);

        var result = await controller.GetSession(_tenants.Object, CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<SessionInfo>().Subject;
    }
}
