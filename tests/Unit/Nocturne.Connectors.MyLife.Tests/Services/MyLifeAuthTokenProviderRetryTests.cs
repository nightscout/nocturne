using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Services;

/// <summary>
///     MyLife sign-in runs on the shared login retry loop. One login attempt is three SOAP calls, so
///     attempts are counted by the GetUser20 call that opens each one.
/// </summary>
public class MyLifeAuthTokenProviderRetryTests
{
    [Fact]
    public async Task GetValidTokenAsync_RetriesTransportFailure_AndSucceedsOnSecondAttempt()
    {
        var handler = new SoapHandler
        {
            FailFirstUserLocationWith = new HttpRequestException("connection reset")
        };

        var token = await AuthenticateAsync(handler);

        token.Should().Be("auth-token-1");
        handler.LoginAttempts.Should().Be(2, "a transport failure is worth exactly one more attempt");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenMyLifeAnswersLoginWith401()
    {
        var handler = new SoapHandler { LoginStatus = HttpStatusCode.Unauthorized };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginAttempts.Should().Be(1, "a 401 is a rejection, and retrying it risks lockout");
    }

    /// <summary>
    ///     MyLife's own bad-credentials answer: HTTP 200 carrying a LoginResult with no auth token.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenLoginReturnsNoAuthToken()
    {
        var handler = new SoapHandler { LoginResultJson = "{}" };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginAttempts.Should().Be(1, "a login answered without a token is a rejected credential");
    }

    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenMyLifeAnswersLoginWith403()
    {
        var handler = new SoapHandler { LoginStatus = HttpStatusCode.Forbidden };

        var token = await AuthenticateAsync(handler);

        token.Should().BeNull();
        handler.LoginAttempts.Should().Be(1, "a refused request is not transient");
    }

    /// <summary>
    ///     A member-supplied ServiceUrl the SOAP client refuses to send to: no request is made, so
    ///     nothing about a second attempt could differ.
    /// </summary>
    [Fact]
    public async Task GetValidTokenAsync_DoesNotRetry_WhenServiceUrlIsUnusable()
    {
        var handler = new SoapHandler();

        var token = await AuthenticateAsync(handler, serviceUrl: "http://insecure.example.com");

        token.Should().BeNull();
        handler.LoginAttempts.Should().Be(1, "an unusable service URL is a config fault, not a transient one");
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
        var handler = new SoapHandler { LoginStatus = HttpStatusCode.ServiceUnavailable };

        var token = await AuthenticateAsync(handler, maxRetryAttempts: maxRetryAttempts);

        token.Should().BeNull();
        handler.LoginAttempts.Should().Be(maxRetryAttempts, "the configured attempt budget is what gets spent");
    }

    private static async Task<string?> AuthenticateAsync(
        SoapHandler handler, string serviceUrl = "", int maxRetryAttempts = 3)
    {
        using var httpClient = new HttpClient(handler);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        var retryDelay = new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var provider = new MyLifeAuthTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<MyLifeConnectorConfiguration>(null, null, null),
            tenantAccessor.Object,
            new MyLifeSoapClient(httpClient, NullLogger<MyLifeSoapClient>.Instance),
            new MyLifeSessionCache(),
            NullLogger<MyLifeAuthTokenProvider>.Instance,
            retryDelay.Object);

        var config = new MyLifeConnectorConfiguration
        {
            Username = "someone@example.com",
            Password = "hunter2",
            ServiceUrl = serviceUrl,
            MaxRetryAttempts = maxRetryAttempts
        };

        return await provider.GetValidTokenAsync(config, CancellationToken.None);
    }

    /// <summary>Answers the three SOAP calls of a MyLife login, with per-test failure injection.</summary>
    private sealed class SoapHandler : HttpMessageHandler
    {
        private const string ServiceUrl = "https://svc.mylife-software.net";

        public HttpRequestException? FailFirstUserLocationWith { get; init; }
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;
        public string LoginResultJson { get; init; } = "{\"AuthToken\":\"auth-token-1\",\"UserId\":\"u1\"}";

        /// <summary>Each login attempt opens with a GetUser20 call, so these count attempts.</summary>
        public int LoginAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.Headers.TryGetValues("SOAPAction", out var values)
                ? values.FirstOrDefault()
                : null;

            if (action == MyLifeConstants.SoapActions.GetUser20)
            {
                LoginAttempts++;
                if (FailFirstUserLocationWith != null && LoginAttempts == 1)
                    throw FailFirstUserLocationWith;

                return Task.FromResult(Envelope(
                    HttpStatusCode.OK,
                    "GetUser20Result",
                    $"{{\"Country20\":{{\"ServiceUrl\":\"{ServiceUrl}\"}}}}"));
            }

            if (action == MyLifeConstants.SoapActions.Login)
                return Task.FromResult(LoginStatus == HttpStatusCode.OK
                    ? Envelope(HttpStatusCode.OK, "LoginResult", LoginResultJson)
                    : new HttpResponseMessage(LoginStatus) { Content = new StringContent(string.Empty) });

            if (action == MyLifeConstants.SoapActions.SyncPatientList)
                return Task.FromResult(Envelope(
                    HttpStatusCode.OK, "SyncPatientListResult", "[{\"OnlinePatientId\":\"p1\"}]"));

            throw new InvalidOperationException($"Unexpected SOAP action: {action}");
        }

        private static HttpResponseMessage Envelope(HttpStatusCode status, string element, string payload)
        {
            var xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                "<s:Body>" +
                $"<{element}>{System.Security.SecurityElement.Escape(payload)}</{element}>" +
                "</s:Body>" +
                "</s:Envelope>";

            return new HttpResponseMessage(status) { Content = new StringContent(xml) };
        }
    }
}
