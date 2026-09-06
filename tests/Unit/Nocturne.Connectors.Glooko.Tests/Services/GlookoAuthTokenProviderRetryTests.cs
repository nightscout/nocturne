using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
///     Glooko sign-in runs on the shared login retry loop, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     buys another attempt for a transport failure — and none at all for a rejected credential,
///     which is what would otherwise walk a tenant into vendor-side lockout.
/// </summary>
public class GlookoAuthTokenProviderRetryTests
{
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransportFailure_AndSucceedsOnSecondAttempt()
    {
        var handler = new ScriptedHandler(
            _ => throw new HttpRequestException("connection reset"),
            _ => SignInSuccess());

        var token = await AuthenticateAsync(handler);

        token.Should().Be($"{GlookoConstants.SessionCookieName}=session-abc");
        handler.CallCount.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenGlookoRejectsTheCredentials()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"Invalid Email or password.\"}")
            });

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.CallCount.Should().Be(1, "retrying a rejected credential cannot help and risks lockout");
    }

    /// <summary>
    ///     V3 declines a sign-in in the body — 200 with { success, two_fa_required } and no
    ///     Set-Cookie — so the status line alone would read it as a success worth retrying.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenV3SignInReturnsNoSessionCookie()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":false,\"two_fa_required\":false}")
            });

        var token = await AuthenticateAsync(handler, useV3Api: true);

        token.Should().BeNull();
        handler.CallCount.Should().Be(1, "a sign-in Glooko declined in the body cannot succeed on a retry");
    }

    /// <summary>
    ///     The configured MaxRetryAttempts is what the login loop spends, so a tenant lowering or
    ///     raising it changes how many times the connector authenticates.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task GetValidTokenAsync_SpendsExactlyMaxRetryAttempts_OnAPersistentRetryableError(
        int maxRetryAttempts)
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("upstream unavailable")
            });

        var token = await AuthenticateAsync(handler, maxRetryAttempts: maxRetryAttempts);

        token.Should().BeNull();
        handler.CallCount.Should().Be(maxRetryAttempts, "the configured attempt budget is what gets spent");
    }

    private static async Task<string?> AuthenticateAsync(
        ScriptedHandler handler, bool useV3Api = false, int maxRetryAttempts = 3)
    {
        using var httpClient = new HttpClient(handler);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var provider = new GlookoAuthTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            tenantAccessor.Object,
            NullLogger<GlookoAuthTokenProvider>.Instance,
            retryDelay.Object);

        // V2 sign-in returns the user data inline, so one request is a whole login attempt.
        var config = new GlookoConnectorConfiguration
        {
            Email = "someone@example.com",
            Password = "hunter2",
            UseV3Api = useV3Api,
            MaxRetryAttempts = maxRetryAttempts
        };

        return await provider.GetValidTokenAsync(config, CancellationToken.None);
    }

    private static HttpResponseMessage SignInSuccess()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"userLogin\":{\"glookoCode\":\"blue-duke-4165\"}}")
        };
        response.Headers.TryAddWithoutValidation(
            "Set-Cookie", $"{GlookoConstants.SessionCookieName}=session-abc; Path=/; HttpOnly");
        return response;
    }

    /// <summary>Answers each request from the next step of a script, and counts the requests.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
        : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var step = steps[Math.Min(index, steps.Length - 1)];
            return Task.FromResult(step(request));
        }
    }
}
