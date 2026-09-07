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
/// A V3 sign-in succeeds (cookie) but the account code comes from a follow-up
/// <c>/api/v3/session/users</c> call. Every patient-scoped data URL is built from that code, so a
/// session without it is unusable — caching it as a success poisons the token cache (the cookie
/// stays valid, so the sync never re-authenticates and logs "Missing Glooko user code" forever).
/// The sign-in must therefore fail, and retry, when the code cannot be resolved.
/// </summary>
public class GlookoAuthTokenProviderV3CodeTests
{
    [Fact]
    public async Task GetValidTokenAsync_V3ProfileHasNoCode_FailsAndRetries()
    {
        var handler = new V3Handler(NoCodeProfile, NoCodeProfile);

        var token = await AuthenticateAsync(handler, maxRetryAttempts: 2);

        token.Should().BeNull("a session with no Glooko code is unusable and must not be cached");
        handler.SignInCount.Should().Be(2, "the whole attempt budget is spent re-authenticating");
        handler.ProfileCount.Should().Be(2, "each attempt re-fetches the profile");
    }

    [Fact]
    public async Task GetValidTokenAsync_V3ProfileCodeArrivesOnRetry_Recovers()
    {
        // First profile fetch yields no code (transient), the second carries it.
        var handler = new V3Handler(NoCodeProfile, CodeProfile);

        var token = await AuthenticateAsync(handler, maxRetryAttempts: 3);

        token.Should().Be($"{GlookoConstants.SessionCookieName}=session-abc");
        handler.SignInCount.Should().Be(2, "it re-authenticated once after the code-less profile");
    }

    private static async Task<string?> AuthenticateAsync(V3Handler handler, int maxRetryAttempts)
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

        var config = new GlookoConnectorConfiguration
        {
            Email = "someone@example.com",
            Password = "hunter2",
            UseV3Api = true,
            MaxRetryAttempts = maxRetryAttempts,
        };

        return await provider.GetValidTokenAsync(config, CancellationToken.None);
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
