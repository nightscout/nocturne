using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// A refresh that fails because the database cannot be read says nothing about the refresh token, so
/// it must not end the session or touch its cookies.
/// </summary>
[Trait("Category", "Unit")]
public class SessionCookieRefreshFailureTests
{
    private readonly OidcOptions _oidc = new();

    public static TheoryData<Exception> InfrastructureFailures => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted"),
        new OperationCanceledException(),
    };

    private SessionCookieHandler HandlerRefreshing(Func<Task<OidcTokenResponse?>> refresh)
    {
        var auth = new Mock<IOidcAuthService>();
        auth.Setup(a => a.RefreshSessionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(refresh);

        var provider = new ServiceCollection()
            .AddSingleton<IJwtService>(new JwtService(
                Options.Create(new JwtOptions
                {
                    SecretKey = "session-cookie-refresh-test-secret-key-32+chars",
                    Issuer = "nocturne",
                    Audience = "nocturne-api",
                }),
                NullLogger<JwtService>.Instance,
                TimeProvider.System))
            .AddSingleton(auth.Object)
            .BuildServiceProvider();

        return new SessionCookieHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionCookieHandler>.Instance,
            Options.Create(_oidc));
    }

    private DefaultHttpContext ContextWithExpiredAccessAndRefreshCookies()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Cookie"] =
            $"{_oidc.Cookie.AccessTokenName}=expired; {_oidc.Cookie.RefreshTokenName}=refresh";
        return context;
    }

    private DefaultHttpContext ContextWithRefreshCookieOnly()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Cookie"] = $"{_oidc.Cookie.RefreshTokenName}=refresh";
        return context;
    }

    [Theory]
    [MemberData(nameof(InfrastructureFailures))]
    public async Task A_refresh_that_hits_a_database_failure_propagates_and_leaves_the_cookies(Exception failure)
    {
        var handler = HandlerRefreshing(() => Task.FromException<OidcTokenResponse?>(failure));
        var context = ContextWithExpiredAccessAndRefreshCookies();

        var authenticate = () => handler.AuthenticateAsync(context);

        (await authenticate.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        context.Response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Theory]
    [MemberData(nameof(InfrastructureFailures))]
    public async Task A_refresh_with_no_access_cookie_that_hits_a_database_failure_propagates(Exception failure)
    {
        var handler = HandlerRefreshing(() => Task.FromException<OidcTokenResponse?>(failure));
        var context = ContextWithRefreshCookieOnly();

        var authenticate = () => handler.AuthenticateAsync(context);

        (await authenticate.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        context.Response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Fact]
    public async Task A_refresh_token_that_is_revoked_or_expired_is_not_authenticated()
    {
        var handler = HandlerRefreshing(() => Task.FromResult<OidcTokenResponse?>(null));
        var context = ContextWithExpiredAccessAndRefreshCookies();

        var result = await handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
        context.Response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Fact]
    public async Task A_refresh_that_faults_for_a_reason_other_than_the_database_is_not_authenticated()
    {
        var handler = HandlerRefreshing(() => Task.FromException<OidcTokenResponse?>(new FormatException("bad token")));
        var context = ContextWithExpiredAccessAndRefreshCookies();

        var result = await handler.AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
        result.ShouldSkip.Should().BeFalse();
    }
}
