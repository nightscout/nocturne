using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers the in-band Authorize wiring on <see cref="DataHub"/>: a token is routed through
/// <see cref="IHubTokenAuthorizer"/> against the connection's immutable tenant, and only an
/// authorized token joins the tenant-scoped "authorized" group.
/// </summary>
[Trait("Category", "Unit")]
public class DataHubAuthorizeTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string ConnectionId = "conn-1";

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static (DataHub hub, Mock<IGroupManager> groups, Mock<IHubTokenAuthorizer> authorizer) CreateHub()
    {
        var authorizer = new Mock<IHubTokenAuthorizer>();
        var hub = new DataHub(NullLogger<DataHub>.Instance, authorizer.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantContext"] =
            new TenantContext(Tenant, "default", "Default", IsActive: true);

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns(ConnectionId);
        callerContext.SetupGet(c => c.Features).Returns(features);

        var groups = new Mock<IGroupManager>();

        hub.Context = callerContext.Object;
        hub.Groups = groups.Object;
        return (hub, groups, authorizer);
    }

    [Fact]
    public async Task Authorize_with_token_accepted_by_authorizer_joins_tenant_authorized_group()
    {
        var (hub, groups, authorizer) = CreateHub();
        authorizer
            .Setup(a => a.IsTokenAuthorizedAsync("oauth-jwt", Tenant, OAuthScopes.GlucoseRead))
            .ReturnsAsync(true);

        var result = await hub.Authorize(new AuthorizeRequest { Token = "oauth-jwt" });

        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, $"{Tenant}:authorized", It.IsAny<CancellationToken>()),
            Times.Once);
        System.Text.Json.JsonSerializer.Serialize(result).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task Authorize_with_rejected_token_does_not_join_group()
    {
        var (hub, groups, authorizer) = CreateHub();
        authorizer
            .Setup(a => a.IsTokenAuthorizedAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var result = await hub.Authorize(new AuthorizeRequest { Token = "bad-token" });

        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        System.Text.Json.JsonSerializer.Serialize(result).Should().Contain("\"success\":false");
    }

    [Fact]
    public async Task Authorize_passes_the_connections_tenant_to_the_authorizer()
    {
        var (hub, _, authorizer) = CreateHub();
        authorizer
            .Setup(a => a.IsTokenAuthorizedAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        await hub.Authorize(new AuthorizeRequest { Token = "tok" });

        // The tenant handed to the authorizer is the connection's resolved tenant — a token
        // from another tenant can never be checked against anything else.
        authorizer.Verify(
            a => a.IsTokenAuthorizedAsync("tok", Tenant, OAuthScopes.GlucoseRead),
            Times.Once);
    }
}
