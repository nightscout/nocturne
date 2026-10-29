using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Mcp;
using Nocturne.API.Models.OAuth;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// RFC 9207: the <c>iss</c> on an authorization response is the issuer the authorization server
/// metadata reports, and an OAuth client (the MCP SDK among them) refuses a response where they
/// differ.
/// </summary>
public class OAuthIssuerAgreementTests
{
    private const string ClientId = "mcp-client";
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";

    [Theory]
    [InlineData(null, "rhys.example.test", "https://rhys.example.test")]
    [InlineData("example.test", "rhys.example.test", "https://rhys.example.test")]
    [InlineData("example.test", "example.test", "https://example.test")]
    [InlineData("localhost:1612", "rhys.localhost:1612", "https://rhys.localhost:1612")]
    public async Task Both_documents_and_the_authorize_redirect_name_the_tenant_hosts_own_origin(
        string? baseDomain, string host, string expectedIssuer)
    {
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new BaseDomainOptions { BaseDomain = baseDomain ?? "" }))
            .BuildServiceProvider();

        var metadata = new WellKnownController(
            Options.Create(new JwtOptions()),
            services.GetRequiredService<IOptions<BaseDomainOptions>>(),
            Options.Create(new McpOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = Request(services, host) },
        };
        var server = ((OkObjectResult)metadata.GetOAuthMetadata().Result!).Value
            .Should().BeOfType<OAuthAuthorizationServerMetadata>().Subject;
        var issuer = server.Issuer;
        var resource = ((OkObjectResult)metadata.GetOAuthProtectedResourceMetadata().Result!).Value
            .Should().BeOfType<OAuthProtectedResourceMetadata>().Subject;

        resource.AuthorizationServers.Should().Equal(expectedIssuer);
        new[] { server.AuthorizationEndpoint, server.TokenEndpoint, server.RegistrationEndpoint!, server.JwksUri }
            .Should().OnlyContain(endpoint => endpoint.StartsWith(expectedIssuer + "/"));

        var redirect = (RedirectResult)await Authorizer(services, host).ApproveConsent(new ConsentApprovalRequest
        {
            ClientId = ClientId,
            RedirectUri = RedirectUri,
            Scope = Scope.GlucoseRead,
            CodeChallenge = "challenge",
            Approved = true,
        });

        issuer.Should().Be(expectedIssuer);
        System.Web.HttpUtility.ParseQueryString(new Uri(redirect.Url).Query)["iss"].Should().Be(issuer);
    }

    private static DefaultHttpContext Request(IServiceProvider services, string host)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(host);
        http.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.CreateVersion7(),
        };
        http.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string> { Scope.FullAccess };
        return http;
    }

    private static OAuthController Authorizer(IServiceProvider services, string host)
    {
        var clients = new Mock<IOAuthClientService>();
        clients.Setup(s => s.GetClientAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo { Id = Guid.CreateVersion7(), ClientId = ClientId });
        clients.Setup(s => s.ValidateRedirectUriAsync(ClientId, RedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tokens = new Mock<IOAuthTokenService>();
        tokens.Setup(s => s.GenerateAuthorizationCodeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("code");

        return new OAuthController(
            clients.Object,
            Mock.Of<IOAuthGrantService>(),
            tokens.Object,
            Mock.Of<IOAuthDeviceCodeService>(),
            Mock.Of<ISubjectService>(),
            Mock.Of<IJwtService>(),
            Mock.Of<IOAuthTokenRevocationCache>(),
            NullLogger<OAuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = Request(services, host) },
        };
    }
}
