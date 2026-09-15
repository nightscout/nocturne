using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Services;

/// <summary>
///     Twiist's Cognito login runs on the shared login retry loop, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     buys another attempt for a transport failure — and none at all for a rejected credential.
/// </summary>
public class TwiistAuthTokenProviderRetryTests
{
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransportFailure_AndSucceedsOnSecondAttempt()
    {
        var handler = new CognitoHandler
        {
            FailFirstLoginWith = new HttpRequestException("connection reset")
        };

        var token = await AuthenticateAsync(handler);

        token.Should().Be("access-token-1");
        handler.LoginCalls.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenCognitoRejectsTheCredentials(
        HttpStatusCode status)
    {
        var handler = new CognitoHandler { LoginStatus = status };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(1, "retrying a rejected credential cannot help and risks lockout");
    }

    /// <summary>
    ///     Cognito answers a challenge (new password, MFA) with 200 and no AuthenticationResult.
    ///     Nothing the connector holds can satisfy one, so the challenge name is the only evidence of
    ///     why this tenant will never authenticate again.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_AndNamesTheChallenge_WhenCognitoIssuesOne()
    {
        var handler = new CognitoHandler { LoginBody = "{\"ChallengeName\":\"SOFTWARE_TOKEN_MFA\"}" };
        var logger = new RecordingLogger();

        var token = await AuthenticateAsync(handler, logger: logger);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(1, "a challenge is Cognito's answer, not a transient failure");
        logger.Errors.Should().ContainMatch("*SOFTWARE_TOKEN_MFA*");
    }

    /// <summary>
    ///     The configured MaxRetryAttempts is what the login loop spends.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task GetValidTokenAsync_SpendsExactlyMaxRetryAttempts_OnAPersistentRetryableError(
        int maxRetryAttempts)
    {
        var handler = new CognitoHandler { LoginStatus = HttpStatusCode.ServiceUnavailable };

        var token = await AuthenticateAsync(handler, maxRetryAttempts);

        token.Should().BeNull();
        handler.LoginCalls.Should().Be(maxRetryAttempts, "the configured attempt budget is what gets spent");
    }

    private static async Task<string?> AuthenticateAsync(
        CognitoHandler handler, int maxRetryAttempts = 3, ILogger<TwiistAuthTokenProvider>? logger = null)
    {
        using var httpClient = new HttpClient(handler);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        using var provider = new TwiistAuthTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null),
            tenantAccessor.Object,
            logger ?? NullLogger<TwiistAuthTokenProvider>.Instance,
            retryDelay.Object);

        var config = new TwiistConnectorConfiguration
        {
            Username = "someone@example.com",
            Password = "hunter2",
            MaxRetryAttempts = maxRetryAttempts
        };

        return await provider.GetValidTokenAsync(config, CancellationToken.None);
    }

    private sealed class RecordingLogger : ILogger<TwiistAuthTokenProvider>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }

    /// <summary>Answers the Cognito InitiateAuth call, with per-test failure injection.</summary>
    private sealed class CognitoHandler : HttpMessageHandler
    {
        public HttpRequestException? FailFirstLoginWith { get; init; }
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;

        public string LoginBody { get; init; } =
            "{\"AuthenticationResult\":{\"AccessToken\":\"access-token-1\",\"RefreshToken\":\"refresh-token-1\"}}";

        public int LoginCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url != $"{TwiistConstants.Cognito.BaseUrl}{TwiistConstants.Cognito.PoolId}")
                throw new InvalidOperationException($"Unexpected Twiist request: {url}");

            LoginCalls++;
            if (FailFirstLoginWith != null && LoginCalls == 1)
                throw FailFirstLoginWith;

            return Task.FromResult(new HttpResponseMessage(LoginStatus)
            {
                Content = new StringContent(LoginStatus == HttpStatusCode.OK
                    ? LoginBody
                    : "{\"__type\":\"NotAuthorizedException\"}")
            });
        }
    }
}
