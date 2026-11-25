using System.Net;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Subject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The OIDC callback after the identity is proved and admitted: the real
/// <see cref="OidcAuthService.CompleteLoginAsync"/> and <see cref="SignInCompletion"/>, whose result
/// is handed to the real <see cref="OidcController.Callback"/>, with one audit log behind all three.
/// </summary>
[Trait("Category", "Unit")]
public class OidcSignInCompletionTests
{
    private const string Ip = "203.0.113.9";
    private const string Agent = "Mozilla/5.0 (X11; Linux x86_64) Firefox/120.0";

    private readonly Mock<ISubjectService> _subjects = new();
    private readonly Mock<ISessionService> _sessions = new();
    private readonly Mock<ITenantMemberService> _members = new();
    private readonly Mock<ITotpService> _totp = new();
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly Mock<IRefreshTokenService> _refreshTokens = new();
    private readonly Mock<IOidcAuthService> _callback = new();
    private readonly List<string> _steps = [];
    private readonly List<(string Type, Guid? Subject, bool Success, string? Error, string? Details)> _rows = [];

    private readonly Subject _subject = new() { Id = Guid.NewGuid(), Name = "Rhys", Email = "user@example.com" };
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly OidcProvider _provider = new()
    {
        Id = Guid.NewGuid(), Name = "Google", IssuerUrl = "https://accounts.google.com", ClientId = "nocturne",
        IsEnabled = true,
    };
    private readonly OidcOptions _options = new();
    private readonly OidcAuthService _service;
    private readonly OidcController _controller;

    public OidcSignInCompletionTests()
    {
        _subjects.Setup(s => s.FindFromOidcAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(_subject);
        _subjects
            .Setup(s => s.FindOrCreateFromOidcAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(_subject);
        _subjects.Setup(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()))
            .Callback(() => _steps.Add("last-login"))
            .Returns(Task.CompletedTask);
        _subjects.Setup(s => s.GetSubjectPermissionsAsync(It.IsAny<Guid>())).ReturnsAsync(new List<string>());
        _subjects.Setup(s => s.GetSubjectRolesAsync(It.IsAny<Guid>())).ReturnsAsync(new List<string>());
        _members.Setup(m => m.IsMemberAsync(_subject.Id, _tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _totp.Setup(t => t.GetSecondFactorRequirementAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.NotEnrolled);
        _sessions
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("session"))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 3600, _subject.Id));
        _audit.Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
            .Callback((string type, Guid? subject, bool success, string? _, string? _, string? error, string? details,
                Guid? _, AuthAuditActor? _, Guid? _) =>
            {
                _steps.Add($"audit:{type}");
                _rows.Add((type, subject, success, error, details));
            })
            .Returns(Task.CompletedTask);

        _service = new OidcAuthService(
            Mock.Of<IOidcProviderService>(),
            _subjects.Object,
            _sessions.Object,
            Mock.Of<IJwtService>(),
            _refreshTokens.Object,
            Mock.Of<IHttpClientFactory>(),
            _members.Object,
            Mock.Of<IMemberInviteService>(),
            Mock.Of<ITenantCreationInviteService>(),
            Mock.Of<ITenantRepository>(),
            new SignInCompletion(_totp.Object, _members.Object, _sessions.Object, _subjects.Object, _audit.Object),
            new EphemeralDataProtectionProvider(),
            Options.Create(_options),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcAuthService>.Instance);

        _controller = new OidcController(
            _callback.Object,
            Mock.Of<IOidcProviderService>(),
            _subjects.Object,
            _audit.Object,
            _members.Object,
            new RecoverySession(Mock.Of<INocturneDbContextFactory>(), Mock.Of<ILoginCodeService>(), Mock.Of<ITenantMemberService>(),
                Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            Options.Create(_options),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcController>.Instance);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(_refreshTokens.Object).AddSingleton(_audit.Object).BuildServiceProvider(),
        };
        http.Connection.RemoteIpAddress = IPAddress.Parse(Ip);
        http.Request.Headers.UserAgent = Agent;
        http.Request.Headers.Cookie = $"{_options.Cookie.StateCookieName}=state";
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
    }

    private Task<OidcCallbackResult> CompleteLoginAsync() =>
        _service.CompleteLoginAsync(
            new OidcAuthService.OidcStateData
            {
                Intent = "login", ReturnUrl = "/reports", ProviderId = _provider.Id, Nonce = "n", TenantSlug = "erik",
                CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            },
            _provider,
            new OidcAuthService.OidcIdTokenClaims { Sub = "google-123", Email = "user@example.com", Sid = "sid" },
            _tenantId,
            Ip,
            Agent);

    /// <summary>The service's own result for this sign-in, handed to the controller's callback.</summary>
    private async Task<IActionResult> CallbackAsync()
    {
        var result = await CompleteLoginAsync();
        _callback
            .Setup(s => s.HandleCallbackAsync(
                "code", "state", "state", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(result);
        return await _controller.Callback("code", "state", null, null);
    }

    private void PresentAHandedOffSession()
    {
        _controller.HttpContext.Request.Headers.Cookie =
            $"{_options.Cookie.StateCookieName}=state; {_options.Cookie.RefreshTokenName}=presented";
        _refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .Callback(() => _steps.Add("end-handed-off"))
            .ReturnsAsync(new RefreshTokenSession(_subject.Id, "previous-session", true));
    }

    private string SetCookie() => _controller.HttpContext.Response.Headers.SetCookie.ToString();

    [Fact]
    public async Task A_sign_in_is_given_a_session_then_last_login_then_one_login_row_that_the_controller_does_not_repeat()
    {
        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/reports");
        _steps.Should().Equal("session", "last-login", "audit:login");
        _sessions.Verify(s => s.IssueSessionAsync(
            _subject.Id,
            new SessionContext("sid", "Linux", Ip, Agent),
            It.IsAny<CancellationToken>()));
        _rows.Should().ContainSingle().Which.Should().Be(
            (AuthAuditEventType.Login, (Guid?)_subject.Id, true, (string?)null, (string?)"""{"method":"oidc"}"""));
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, _subject.Id, true, Ip, Agent, null, """{"method":"oidc"}""", null, null, null));
        SetCookie().Should().Contain($"{_options.Cookie.AccessTokenName}=access")
            .And.Contain($"{SessionCookieExtensions.LastSignInCookieName}=oidc%3A{_provider.Id}");
    }

    [Fact]
    public async Task A_code_still_owed_writes_one_pending_row_then_the_step_up_cookie_and_the_hint()
    {
        _totp.Setup(t => t.GetSecondFactorRequirementAsync(_subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totp.Setup(t => t.CreateStepUpTokenAsync(_subject.Id, _tenantId, false)).ReturnsAsync("step-up-token");

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/auth/login?returnUrl=%2Freports");
        _rows.Should().ContainSingle().Which.Should().Be(
            (AuthAuditEventType.Login, (Guid?)_subject.Id, false, (string?)null,
                (string?)"""{"method":"oidc","secondFactorPending":true}"""));
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, _subject.Id, false, Ip, Agent, null,
            """{"method":"oidc","secondFactorPending":true}""", null, null, null));
        var cookies = _controller.HttpContext.Response.Headers.SetCookie
            .Where(c => !c!.StartsWith(_options.Cookie.StateCookieName)).ToArray();
        cookies.Should().HaveCount(2);
        cookies[0].Should().StartWith($"{SessionCookieExtensions.TotpStepUpCookieName}=step-up-token");
        cookies[1].Should().StartWith($"{SessionCookieExtensions.LastSignInCookieName}=oidc%3A{_provider.Id}");
    }

    [Fact]
    public async Task A_membership_that_ends_between_the_two_checks_is_refused_with_the_providers_own_row_written_once()
    {
        _members.SetupSequence(m => m.IsMemberAsync(_subject.Id, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/auth/login?reason=not_a_member&returnUrl=%2Freports");
        var refused = await _callback.Object.HandleCallbackAsync("code", "state", "state");
        refused.IsAccessDenied.Should().BeTrue();
        refused.RefusalAudited.Should().BeTrue();
        refused.SubjectId.Should().Be(_subject.Id);
        refused.ProviderId.Should().Be(_provider.Id);
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, _subject.Id, false, Ip, Agent, "not_a_member",
            OidcController.NotAMemberDetails(refused), null, null, null), Times.Once);
        _rows.Should().ContainSingle("the controller writes no second row for a refusal the completion wrote");
        _sessions.Verify(s => s.IssueSessionAsync(
            It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _totp.Verify(t => t.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
        SetCookie().Should().NotContain(_options.Cookie.AccessTokenName)
            .And.NotContain(SessionCookieExtensions.LastSignInCookieName);
    }

    [Fact]
    public async Task A_refusal_before_the_subject_is_written_is_still_recorded_by_the_controller()
    {
        _members.Setup(m => m.IsMemberAsync(_subject.Id, _tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CallbackAsync();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/auth/login?reason=not_a_member&returnUrl=%2Freports");
        var refused = await _callback.Object.HandleCallbackAsync("code", "state", "state");
        refused.RefusalAudited.Should().BeFalse();
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, _subject.Id, false, Ip, Agent, "not_a_member",
            OidcController.NotAMemberDetails(refused), null, null, null), Times.Once);
        _rows.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_last_login_write_leaves_an_issued_session_that_is_never_delivered_and_no_login_row()
    {
        _subjects.Setup(s => s.UpdateLastLoginAsync(_subject.Id)).ThrowsAsync(new DbUpdateException("down"));

        var act = CompleteLoginAsync;

        await act.Should().ThrowAsync<DbUpdateException>();
        _steps.Should().Equal("session");
        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_step_up_mint_leaves_the_pending_login_row_with_no_token()
    {
        _totp.Setup(t => t.GetSecondFactorRequirementAsync(_subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totp.Setup(t => t.CreateStepUpTokenAsync(_subject.Id, _tenantId, false))
            .ThrowsAsync(new DbUpdateException("down"));

        var act = CompleteLoginAsync;

        await act.Should().ThrowAsync<DbUpdateException>();
        _rows.Should().ContainSingle().Which.Details.Should().Be("""{"method":"oidc","secondFactorPending":true}""");
    }

    [Fact]
    public async Task Over_a_handed_off_session_the_sign_in_is_logged_before_the_replaced_session_is_ended()
    {
        PresentAHandedOffSession();

        await CallbackAsync();

        _steps.Should().Equal("session", "last-login", "audit:login", "end-handed-off", "audit:logout");
        _refreshTokens.Verify(r => r.RevokeSessionForSubjectAsync(_subject.Id, "previous-session", It.IsAny<string>()));
    }

    [Fact]
    public async Task A_failure_ending_the_handed_off_session_leaves_the_login_row_and_last_login_behind_the_error()
    {
        PresentAHandedOffSession();
        _refreshTokens.Setup(r => r.FindOwnerAsync("presented")).ThrowsAsync(new InvalidOperationException("down"));

        var act = CallbackAsync;

        await act.Should().ThrowAsync<InvalidOperationException>();
        _steps.Should().Equal("session", "last-login", "audit:login");
        _rows.Should().ContainSingle().Which.Success.Should().BeTrue();
        _refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
