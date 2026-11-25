using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// A V3 sign-in succeeds (cookie) but the account code comes from a follow-up
/// <c>/api/v3/session/users</c> call. Every patient-scoped data URL is built from that code, so a
/// session without it is unusable — caching it as a success poisons the token cache (the cookie
/// stays valid, so the sync never re-authenticates and logs "Missing Glooko user code" forever).
/// The sign-in therefore fails when the code cannot be resolved, and that failure is final.
/// </summary>
public class GlookoAuthTokenProviderV3CodeTests
{
    [Fact]
    public async Task GetValidTokenAsync_V3ProfileHasNoCode_FailsWithoutSigningInAgain()
    {
        var handler = new V3Handler(NoCodeProfile, CodeProfile);

        var (token, _) = await AuthenticateAsync(handler, maxRetryAttempts: 3);

        token.Should().BeNull("a session with no Glooko code is unusable and must not be cached");
        handler.SignInCount.Should().Be(1);
        handler.ProfileCount.Should().Be(1);
    }

    [Fact]
    public async Task GetValidTokenAsync_V3ProfileHasTheCode_ReturnsTheSession()
    {
        var handler = new V3Handler(CodeProfile);

        var (token, _) = await AuthenticateAsync(handler, maxRetryAttempts: 3);

        token.Should().Be($"{GlookoConstants.SessionCookieName}=session-abc");
        handler.SignInCount.Should().Be(1);
    }

    /// <summary>
    /// On a client with no retry handler, which is how credential verification is registered, a
    /// profile fetch that hit a transport failure or a retryable status is the loop's to retry.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetValidTokenAsync_NoHandler_V3ProfileUnavailableOnce_SignsInAgainAndRecovers(
        HttpStatusCode status)
    {
        var handler = new V3Handler(() => new HttpResponseMessage(status), CodeProfile);

        var (token, _) = await AuthenticateAsync(handler, maxRetryAttempts: 3);

        token.Should().Be($"{GlookoConstants.SessionCookieName}=session-abc");
        handler.SignInCount.Should().Be(2);
        handler.ProfileCount.Should().Be(2);
    }

    [Fact]
    public async Task GetValidTokenAsync_NoHandler_V3ProfileConnectionResetOnce_SignsInAgainAndRecovers()
    {
        var handler = new V3Handler(() => throw new HttpRequestException("connection reset"), CodeProfile);

        var (token, _) = await AuthenticateAsync(handler, maxRetryAttempts: 3);

        token.Should().Be($"{GlookoConstants.SessionCookieName}=session-abc");
        handler.SignInCount.Should().Be(2);
        handler.ProfileCount.Should().Be(2);
    }

    [Fact]
    public async Task GetValidTokenAsync_NoHandler_V3ProfileUnavailableEveryTime_SpendsTheBudgetAndSaysNothingAboutTheCredentials()
    {
        var handler = new V3Handler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var (token, reason) = await AuthenticateAsync(handler, maxRetryAttempts: 3);

        token.Should().BeNull();
        handler.SignInCount.Should().Be(3);
        reason.Should().Contain("usually temporary").And.NotContain("did not accept this sign-in");
    }

    /// <summary>
    /// A profile the source refuses is still not a verdict on the credentials the sign-in just
    /// proved, and nothing a second sign-in could change.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetValidTokenAsync_V3ProfileRefused_IsFinalAndNeverNamesTheCredentials(HttpStatusCode status)
    {
        var handler = new V3Handler(() => new HttpResponseMessage(status));

        var (token, reason) = await AuthenticateAsync(handler, maxRetryAttempts: 3);

        token.Should().BeNull();
        handler.SignInCount.Should().Be(1);
        handler.ProfileCount.Should().Be(1);
        reason.Should().Contain("usually temporary").And.NotContain("did not accept this sign-in");
    }

    private static async Task<(string? Token, string Reason)> AuthenticateAsync(
        V3Handler handler, int maxRetryAttempts)
    {
        using var httpClient = new HttpClient(handler);

        var tenantAccessor = ResolvedTenant.Accessor();

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var provider = new GlookoAuthTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            tenantAccessor,
            NullLogger<GlookoAuthTokenProvider>.Instance,
            retryDelay.Object);

        var config = new GlookoConnectorConfiguration
        {
            Email = "someone@example.com",
            Password = "hunter2",
            UseV3Api = true,
            MaxRetryAttempts = maxRetryAttempts,
        };

        var token = await provider.GetValidTokenAsync(config, CancellationToken.None);
        return (token, provider.SignInFailureReason);
    }

    private static HttpResponseMessage SignInCookie()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true,\"two_fa_required\":false}"),
        };
        response.Headers.TryAddWithoutValidation(
            "Set-Cookie", $"{GlookoConstants.SessionCookieName}=session-abc; Path=/; HttpOnly");
        return response;
    }

    private static HttpResponseMessage CodeProfile() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"currentUser\":{\"glookoCode\":\"blue-duke-4165\",\"meterUnits\":\"mgdl\"}}"),
    };

    private static HttpResponseMessage NoCodeProfile() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"currentUser\":{\"meterUnits\":\"mgdl\"}}"),
    };

    /// <summary>
    /// Routes sign-in requests to a fresh cookie response and <c>session/users</c> requests to the
    /// next profile factory (repeating the last), counting each. Factories, not instances, because an
    /// <see cref="HttpResponseMessage"/> body can only be read once.
    /// </summary>
    private sealed class V3Handler(params Func<HttpResponseMessage>[] profiles) : HttpMessageHandler
    {
        public int SignInCount;
        public int ProfileCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("sign_in", StringComparison.OrdinalIgnoreCase))
            {
                SignInCount++;
                return Task.FromResult(SignInCookie());
            }

            var index = Math.Min(ProfileCount, profiles.Length - 1);
            ProfileCount++;
            return Task.FromResult(profiles[index]());
        }
    }
}
