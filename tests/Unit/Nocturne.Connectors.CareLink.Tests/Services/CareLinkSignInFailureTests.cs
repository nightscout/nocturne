using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Services;

/// <summary>
///     What a CareLink sync that could not sign in tells the tenant. The token provider is the only
///     place that saw why, so its classification has to reach the result: a source that never
///     answered has no credential to fix.
/// </summary>
public class CareLinkSignInFailureTests
{
    [Fact]
    public async Task Sync_WhenCareLinkCannotBeReached_DoesNotBlameTheCredentials()
    {
        var result = await SyncAgainst(new CareLinkLoginHandler { Unreachable = true });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary")
            .And.NotContain("password")
            .And.NotBe("Authentication failed");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    [Fact]
    public async Task Sync_WhenCareLinkRefusesTheSignIn_SendsTheTenantToTheirCredentials()
    {
        // Auth0 answers a wrong password with 200 and an error page.
        var result = await SyncAgainst(new CareLinkLoginHandler());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    [Fact]
    public async Task Sync_WhenTheRefreshTokenIsRevokedAndNoPasswordIsSet_SendsTheTenantToTheirCredentials()
    {
        // Only re-authorizing clears a refresh token Auth0 answered invalid_grant, and CareLink's
        // CAPTCHA leaves many members with no password configured to fall back on.
        var result = await SyncAgainst(
            new CareLinkLoginHandler(),
            new CareLinkConnectorConfiguration
            {
                Username = "user@example.com", RefreshToken = "revoked-token", Server = "EU"
            });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    private static async Task<SyncResult> SyncAgainst(
        CareLinkLoginHandler handler, CareLinkConnectorConfiguration? config = null)
    {
        using var authClient = new HttpClient(handler, disposeHandler: false);
        using var serviceClient = new HttpClient(handler, disposeHandler: false);
        var resolver = new ConnectorServerResolver<CareLinkConnectorConfiguration>(
            null, null, CareLinkConstants.Servers.Eu);

        using var provider = new TestableProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant(),
            NullLogger<CareLinkAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            handler);
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetSecretsAsync("CareLink", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var service = new CareLinkConnectorService(
            serviceClient,
            resolver,
            provider,
            configService.Object,
            NullLogger<CareLinkConnectorService>.Instance);

        try
        {
            return await service.SyncDataAsync(
                new SyncRequest { DataTypes = [SyncDataType.Glucose] },
                config ?? new CareLinkConnectorConfiguration
                {
                    Username = "user@example.com", Password = "hunter2", Server = "EU"
                },
                CancellationToken.None);
        }
        finally
        {
            handler.Dispose();
        }
    }

    private static ITenantAccessor ResolvedTenant()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());
        return tenant.Object;
    }

    /// <summary>Routes the provider's own auth-flow requests through the test handler.</summary>
    private sealed class TestableProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<CareLinkConnectorConfiguration> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger<CareLinkAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy,
        HttpMessageHandler handler)
        : CareLinkAuthTokenProvider(httpClient, tokenCache, serverResolver, tenantAccessor, logger, retryDelayStrategy)
    {
        protected override CareLinkAuthFlowService CreateAuthFlow() => new(NullLogger.Instance, handler);
    }

    /// <summary>
    ///     Carries the Auth0 PKCE flow to the credential POST, which answers with Auth0's wrong-password
    ///     page, and answers every refresh-token grant with Auth0's invalid_grant; or, when
    ///     <see cref="Unreachable"/>, fails every request before any answer arrives.
    /// </summary>
    private sealed class CareLinkLoginHandler : HttpMessageHandler
    {
        private const string LoginHost = "carelink-login.example";
        private const string SsoConfigUrl = $"https://{LoginHost}/configs/carepartner_auth0_sso_config.json";
        private const string FormActionUrl = $"https://{LoginHost}/u/login";
        private const string TokenUrl = $"https://{LoginHost}/oauth/token";

        public bool Unreachable { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
                throw new HttpRequestException("No such host is known");

            var url = request.RequestUri!.ToString();

            if (url.Contains("/discover/", StringComparison.Ordinal))
                return Answer($$"""
                    {"CP":[{"region":"EU","Auth0SSOConfiguration":"{{SsoConfigUrl}}"}]}
                    """);

            if (url == SsoConfigUrl)
                return Answer($$"""
                    {
                      "server": { "hostname": "{{LoginHost}}", "port": 443, "prefix": "" },
                      "client": {
                        "client_id": "client-1",
                        "scope": "profile openid offline_access",
                        "audience": "carepartner.patient.ous",
                        "redirect_uri": "com.medtronic.carepartner:/sso"
                      },
                      "system_endpoints": {
                        "authorization_endpoint_path": "/authorize",
                        "token_endpoint_path": "/oauth/token"
                      }
                    }
                    """);

            if (url.StartsWith($"https://{LoginHost}/authorize", StringComparison.Ordinal))
                return Answer($"<html><form action=\"{FormActionUrl}\" method=\"post\">"
                              + "<input type=\"hidden\" name=\"state\" value=\"state-1\" /></form></html>");

            if (url == FormActionUrl)
                return Answer("Wrong username or password");

            if (url == TokenUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":"invalid_grant","error_description":"Unknown or invalid refresh token."}""",
                        Encoding.UTF8, "application/json")
                });

            throw new InvalidOperationException($"Unexpected CareLink request: {url}");
        }

        private static Task<HttpResponseMessage> Answer(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            });
    }
}
