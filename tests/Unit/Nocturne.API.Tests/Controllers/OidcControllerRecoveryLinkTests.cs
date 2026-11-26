using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Linking an identity provider as a recovery session's one enrolment, through the link callback
/// every provider already has registered.
/// </summary>
[Trait("Category", "Unit")]
public class OidcControllerRecoveryLinkTests : IDisposable
{
    private sealed class SharedSqliteFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private const string RecoveryState = "recovery-state";

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _providerId = Guid.CreateVersion7();
    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
    private readonly LoginCodeService _loginCodes;
    private readonly OidcOptions _options = new()
    {
        Cookie = new CookieSettings
        {
            LinkStateCookieName = ".Nocturne.OidcLinkState",
            AccessTokenName = ".Nocturne.AccessToken",
            RefreshTokenName = ".Nocturne.RefreshToken",
            StateCookieName = ".Nocturne.OidcState",
            Secure = true,
            Path = "/",
        },
    };
    private readonly OidcController _controller;

    public OidcControllerRecoveryLinkTests()
    {
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _loginCodes = new LoginCodeService(jwt.Object, new Mock<IAuthAuditService>().Object, TimeProvider.System);

        using (var seed = _db.CreateContext(Guid.Empty))
        {
            seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "alpha", DisplayName = "Alpha" });
            seed.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Rhys", IsActive = true });
            seed.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _subjectId });
            seed.SaveChanges();
        }

        _authService.Setup(s => s.IsRecoveryState(RecoveryState)).Returns(true);

        _controller = new OidcController(
            _authService.Object,
            new Mock<IOidcProviderService>().Object,
            new Mock<ISubjectService>().Object,
            _auditService.Object,
            new Mock<ITenantMemberService>().Object,
            new RecoverySession(
                new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId), _loginCodes,
                new TenantMemberService(
                    new TenantMembershipRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId), AccountLinkRoutes.Off), TimeProvider.System),
                Options.Create(_options), NullLogger<RecoverySession>.Instance),
            Options.Create(_options),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }),
            NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public void Dispose() => _db.Dispose();

    private async Task<string> IssueAsync(LoginCodePurpose purpose)
    {
        await using var db = _db.CreateContext(_tenantId);
        return (await _loginCodes.IssueAsync(db, _subjectId, purpose, null, null, new AuthAuditActor(null, "test"))).Code;
    }

    private void PresentCookies(string? session, string? linkState = RecoveryState)
    {
        var cookies = new List<string>();
        if (session is not null)
            cookies.Add($"{RecoverySession.CookieName}={session}");
        if (linkState is not null)
            cookies.Add($"{_options.Cookie.LinkStateCookieName}={linkState}");
        _controller.HttpContext.Request.Headers.Cookie = string.Join("; ", cookies);
    }

    private async Task<bool> IsLiveAsync(string code)
    {
        await using var db = _db.CreateContext(_tenantId);
        return await _loginCodes.FindLiveAsync(db, code, [LoginCodePurpose.RecoverySession]) is not null;
    }

    private void LinkAnswers(OidcLinkResult result) =>
        _authService
            .Setup(s => s.HandleRecoveryLinkCallbackAsync("code", RecoveryState, RecoveryState, It.IsAny<Guid>()))
            .ReturnsAsync(result);

    private Task<IActionResult> CallbackAsync() => _controller.LinkCallback("code", RecoveryState, null, null);

    #region Start

    [Fact]
    public void TheStart_IsAPostAndNeverAGet()
    {
        var start = typeof(OidcController).GetMethod(nameof(OidcController.StartRecoveryLink))!;

        start.GetCustomAttributes<HttpPostAttribute>().Should().ContainSingle();
        start.GetCustomAttributes<HttpGetAttribute>().Should().BeEmpty();
    }

    [Fact]
    public async Task TheStart_WithoutARecoverySession_Is401()
    {
        var result = await _controller.StartRecoveryLink(new OidcRecoveryLinkRequest { ProviderId = _providerId });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(401);
        _authService.Verify(s => s.GenerateRecoveryLinkAuthorizationUrlAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task TheStart_WithASignedInSessionButNoRecoverySession_Is401()
    {
        _controller.HttpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _subjectId };

        var result = await _controller.StartRecoveryLink(new OidcRecoveryLinkRequest { ProviderId = _providerId });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task TheStart_BindsTheStateToTheSessionsSubjectWithoutSpendingIt()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        PresentCookies(session, linkState: null);
        _authService
            .Setup(s => s.GenerateRecoveryLinkAuthorizationUrlAsync(_providerId, _subjectId, "/settings", It.IsAny<string?>()))
            .ReturnsAsync(new OidcAuthorizationRequest
            {
                AuthorizationUrl = "https://idp.example/auth?x=1",
                State = "issued-state",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                ProviderId = _providerId,
            });

        var result = await _controller.StartRecoveryLink(
            new OidcRecoveryLinkRequest { ProviderId = _providerId, ReturnUrl = "/settings" });

        ((OidcRecoveryLinkResponse)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!)
            .AuthorizationUrl.Should().Be("https://idp.example/auth?x=1");
        _controller.HttpContext.Response.Headers.SetCookie.ToString()
            .Should().Contain(_options.Cookie.LinkStateCookieName).And.Contain("issued-state");
        (await IsLiveAsync(session)).Should().BeTrue();
    }

    [Fact]
    public async Task TheStart_ForAProviderThatIsNotAvailable_Is400()
    {
        PresentCookies(await IssueAsync(LoginCodePurpose.RecoverySession), linkState: null);
        _authService
            .Setup(s => s.GenerateRecoveryLinkAuthorizationUrlAsync(_providerId, _subjectId, It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("OIDC provider is not enabled"));

        var result = await _controller.StartRecoveryLink(new OidcRecoveryLinkRequest { ProviderId = _providerId });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task TheStart_RefusesAReturnUrlOffTheSite()
    {
        PresentCookies(await IssueAsync(LoginCodePurpose.RecoverySession), linkState: null);

        var result = await _controller.StartRecoveryLink(
            new OidcRecoveryLinkRequest { ProviderId = _providerId, ReturnUrl = "https://evil.example/" });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    #endregion

    #region Callback

    [Fact]
    public async Task TheCallback_LinksForTheSessionsSubjectEndsTheSessionAndSendsTheBrowserToTheOrdinarySignIn()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        var adminCode = await IssueAsync(LoginCodePurpose.Recovery);
        PresentCookies(session);
        var identityId = Guid.CreateVersion7();
        LinkAnswers(OidcLinkResult.Succeeded(identityId, "/settings/account", _providerId));

        var result = await CallbackAsync();

        var redirect = result.Should().BeOfType<RedirectResult>().Subject.Url;
        redirect.Should().StartWith($"/api/auth/oidc/login?provider={_providerId}&returnUrl=");
        Uri.UnescapeDataString(redirect[(redirect.IndexOf("returnUrl=", StringComparison.Ordinal) + 10)..])
            .Should().Be($"/settings/account?{OidcController.RecoveryLinkedIdentityParameter}={identityId}");
        _authService.Verify(s => s.HandleRecoveryLinkCallbackAsync("code", RecoveryState, RecoveryState, _subjectId), Times.Once);
        (await IsLiveAsync(session)).Should().BeFalse();
        await using (var db = _db.CreateContext(_tenantId))
            (await _loginCodes.FindLiveAsync(db, adminCode, [LoginCodePurpose.Recovery])).Should().BeNull();
        _controller.HttpContext.Response.Headers.SetCookie
            .Should().Contain(h => h!.StartsWith($"{RecoverySession.CookieName}=;", StringComparison.Ordinal));
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.OidcIdentityLinked, _subjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task TheCallback_IssuesNoSessionAndRecordsNoSignIn()
    {
        PresentCookies(await IssueAsync(LoginCodePurpose.RecoverySession));
        LinkAnswers(OidcLinkResult.Succeeded(Guid.CreateVersion7(), "/", _providerId));

        await CallbackAsync();

        _controller.HttpContext.Response.Headers.SetCookie
            .Should().NotContain(h => h!.StartsWith(".Nocturne.AccessToken=", StringComparison.Ordinal)
                || h.StartsWith(".Nocturne.RefreshToken=", StringComparison.Ordinal));
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, It.IsAny<Guid?>(), true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task TheCallback_ASecondUseOfTheSession_IsRefusedEvenWithTheCookieReplayed()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        PresentCookies(session);
        LinkAnswers(OidcLinkResult.Succeeded(Guid.CreateVersion7(), "/", _providerId));
        await CallbackAsync();

        PresentCookies(session);
        var second = await CallbackAsync();

        second.Should().BeOfType<RedirectResult>().Which.Url.Should().StartWith("/auth/error").And.EndWith("flow=recovery");
        _authService.Verify(s => s.HandleRecoveryLinkCallbackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task TheCallback_ForAnIdentityTheAccountAlreadyHad_EndsTheSessionWithoutNamingIt()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        PresentCookies(session);
        LinkAnswers(OidcLinkResult.Succeeded(Guid.CreateVersion7(), "/settings/account", _providerId, added: false));

        var result = await CallbackAsync();

        var redirect = result.Should().BeOfType<RedirectResult>().Subject.Url;
        redirect.Should().Be(
            $"/api/auth/oidc/login?provider={_providerId}&returnUrl={Uri.EscapeDataString("/settings/account")}");
        (await IsLiveAsync(session)).Should().BeFalse();
    }

    [Fact]
    public async Task ASessionWhoseMemberWasDeactivated_CannotLinkAProvider()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        await using (var db = _db.CreateContext(Guid.Empty))
        {
            (await db.Subjects.SingleAsync(s => s.Id == _subjectId)).IsActive = false;
            await db.SaveChangesAsync();
        }
        PresentCookies(session);
        LinkAnswers(OidcLinkResult.Succeeded(Guid.CreateVersion7(), "/", _providerId));

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().StartWith("/auth/error").And.EndWith("flow=recovery");
        _authService.Verify(s => s.HandleRecoveryLinkCallbackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TheCallback_WhenTheIdentityIsLinkedToSomeoneElse_LeavesTheSessionUsable()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        PresentCookies(session);
        LinkAnswers(OidcLinkResult.Failed("identity_already_linked", "already linked"));

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Contain("identity_already_linked");
        (await IsLiveAsync(session)).Should().BeTrue("the member can still enrol a passkey or another provider");
        _controller.HttpContext.Response.Headers.SetCookie
            .Should().NotContain(h => h!.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCallback_WhenTheStateNamesAnotherSubject_AttachesNothingAndLeavesTheSessionUsable()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        PresentCookies(session);
        LinkAnswers(OidcLinkResult.Failed("invalid_state", "State subject mismatch"));

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Contain("invalid_state");
        (await IsLiveAsync(session)).Should().BeTrue();
    }

    [Fact]
    public async Task ARecoveryState_NeverCompletesWithASignedInSession()
    {
        PresentCookies(session: null);
        _controller.HttpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _subjectId };
        LinkAnswers(OidcLinkResult.Succeeded(Guid.CreateVersion7(), "/", _providerId));

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().StartWith("/auth/error").And.EndWith("flow=recovery");
        _authService.Verify(s => s.HandleRecoveryLinkCallbackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        _authService.Verify(s => s.HandleLinkCallbackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData(LoginCodePurpose.Recovery)]
    [InlineData(LoginCodePurpose.Login)]
    public async Task ARecoveryState_NeverCompletesWithAnotherPurposesCodeAsTheCookie(LoginCodePurpose purpose)
    {
        PresentCookies(await IssueAsync(purpose));
        LinkAnswers(OidcLinkResult.Succeeded(Guid.CreateVersion7(), "/", _providerId));

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().StartWith("/auth/error").And.EndWith("flow=recovery");
        _authService.Verify(s => s.HandleRecoveryLinkCallbackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ALinkState_NeverCompletesWithARecoverySession()
    {
        var session = await IssueAsync(LoginCodePurpose.RecoverySession);
        PresentCookies(session, linkState: "link-state");

        var result = await _controller.LinkCallback("code", "link-state", null, null);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().StartWith("/auth/login");
        _authService.Verify(s => s.HandleLinkCallbackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        (await IsLiveAsync(session)).Should().BeTrue();
    }

    #endregion
}
