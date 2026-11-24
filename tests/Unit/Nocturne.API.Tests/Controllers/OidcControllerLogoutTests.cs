using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Signing out ends every session the caller signs out of, each whole: the one the presented
/// refresh token belongs to, live, rotated out or revoked, and the session the caller is signed in
/// with. No other session of the subject ends.
/// </summary>
[Trait("Category", "Unit")]
public class OidcControllerLogoutTests
{
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private const string PresentedToken = "presented-refresh-token";

    private readonly Mock<IRefreshTokenService> _refreshTokens = new();
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly OidcOptions _options = new();

    private OidcController Controller(AuthContext? caller, string? refreshCookie)
    {
        var tenantMembers = new Mock<ITenantMemberService>();
        var sessions = new Mock<ISessionService>();
        var subjects = new Mock<ISubjectService>();
        var service = new OidcAuthService(
            new Mock<IOidcProviderService>().Object,
            subjects.Object,
            sessions.Object,
            new Mock<IJwtService>().Object,
            _refreshTokens.Object,
            new Mock<IHttpClientFactory>().Object,
            tenantMembers.Object,
            new Mock<IMemberInviteService>().Object,
            new Mock<ITenantCreationInviteService>().Object,
            new Mock<ITenantRepository>().Object,
            new SignInCompletion(new Mock<ITotpService>().Object, tenantMembers.Object,
                sessions.Object, subjects.Object, new Mock<IAuthAuditService>().Object),
            new EphemeralDataProtectionProvider(),
            Options.Create(_options),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcAuthService>.Instance);

        var controller = new OidcController(
            service,
            new Mock<IOidcProviderService>().Object,
            subjects.Object,
            _audit.Object,
            tenantMembers.Object,
            new RecoverySession(new Mock<INocturneDbContextFactory>().Object, new Mock<ILoginCodeService>().Object,
                tenantMembers.Object, Options.Create(_options), NullLogger<RecoverySession>.Instance),
            Options.Create(_options),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        if (caller is not null)
            controller.HttpContext.Items["AuthContext"] = caller;
        if (refreshCookie is not null)
            controller.HttpContext.Request.Headers.Cookie = $"{_options.Cookie.RefreshTokenName}={refreshCookie}";
        return controller;
    }

    private static AuthContext Session(string sessionId, AuthType authType = AuthType.SessionCookie) => new()
    {
        IsAuthenticated = true,
        AuthType = authType,
        SubjectId = SubjectId,
        SessionId = sessionId,
    };

    [Fact]
    public async Task A_presented_refresh_token_ends_the_whole_session_it_belongs_to_even_once_rotated_out()
    {
        _refreshTokens.Setup(r => r.FindOwnerAsync(PresentedToken))
            .ReturnsAsync(new RefreshTokenSession(SubjectId, "signed-in-session"));

        await Controller(Session("signed-in-session"), PresentedToken).Logout();

        _refreshTokens.Verify(r => r.FindOwnerAsync(PresentedToken), Times.Once);
        _refreshTokens.Verify(r => r.RevokeSessionForSubjectAsync(SubjectId, "signed-in-session", "User logout"), Times.Once);
        _refreshTokens.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_refresh_token_of_another_session_ends_that_session_and_the_signed_in_one_and_no_third()
    {
        _refreshTokens.Setup(r => r.FindOwnerAsync(PresentedToken))
            .ReturnsAsync(new RefreshTokenSession(SubjectId, "presented-session"));

        var controller = Controller(Session("signed-in-session"), PresentedToken);

        var result = await controller.Logout();

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<LogoutResponse>().Which.Success.Should().BeTrue();
        _refreshTokens.Verify(r => r.FindOwnerAsync(PresentedToken), Times.Once);
        _refreshTokens.Verify(r => r.RevokeSessionForSubjectAsync(SubjectId, "presented-session", "User logout"), Times.Once);
        _refreshTokens.Verify(r => r.RevokeSessionForSubjectAsync(SubjectId, "signed-in-session", "User logout"), Times.Once);
        _refreshTokens.VerifyNoOtherCalls();
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.Logout, SubjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Once);
        controller.HttpContext.Response.Headers.SetCookie.Should().Contain(
            header => header!.StartsWith($"{_options.Cookie.RefreshTokenName}=;", StringComparison.Ordinal)
                && header.Contains("expires=Thu, 01 Jan 1970"));
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.LegacyJwt)]
    public async Task With_no_refresh_token_the_session_the_caller_is_signed_in_with_ends(AuthType authType)
    {
        await Controller(Session("signed-in-session", authType), refreshCookie: null).Logout();

        _refreshTokens.Verify(r => r.RevokeSessionForSubjectAsync(SubjectId, "signed-in-session", "User logout"), Times.Once);
        _refreshTokens.Verify(r => r.FindOwnerAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task An_unknown_refresh_token_ends_the_session_the_caller_is_signed_in_with()
    {
        _refreshTokens.Setup(r => r.FindOwnerAsync(PresentedToken)).ReturnsAsync((RefreshTokenSession?)null);

        await Controller(Session("signed-in-session"), PresentedToken).Logout();

        _refreshTokens.Verify(r => r.RevokeSessionForSubjectAsync(SubjectId, "signed-in-session", "User logout"), Times.Once);
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    public async Task A_credential_that_is_not_a_session_ends_no_session(AuthType authType)
    {
        var result = await Controller(Session("signed-in-session", authType), refreshCookie: null).Logout();

        result.Result.Should().BeOfType<OkObjectResult>();
        _refreshTokens.Verify(r => r.RevokeSessionForSubjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_caller_with_neither_a_refresh_token_nor_a_session_is_signed_out_and_ends_nothing()
    {
        var result = await Controller(caller: null, refreshCookie: null).Logout();

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<LogoutResponse>().Which.Success.Should().BeTrue();
        _refreshTokens.VerifyNoOtherCalls();
    }
}
