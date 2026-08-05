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
/// <see cref="IHubTokenAuthorizer"/> against the connection's immutable tenant, only an authorized
/// token joins the tenant-scoped "authorized" group, and Subscribe joins a category group only when
/// the connection's credential holds the scope governing it.
/// </summary>
[Trait("Category", "Unit")]
public class DataHubAuthorizeTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
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
            new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false);

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns(ConnectionId);
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());

        var groups = new Mock<IGroupManager>();

        hub.Context = callerContext.Object;
        hub.Groups = groups.Object;
        return (hub, groups, authorizer);
    }

    private static HubAuthorization Authorized(params string[] scopes) =>
        new(Tenant, OAuthScopes.Normalize(scopes), HubCredentialKind.Subject, Subject);

    [Fact]
    public async Task Authorize_with_token_accepted_by_authorizer_joins_tenant_authorized_group()
    {
        var (hub, groups, authorizer) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync("oauth-jwt", Tenant, OAuthScopes.GlucoseRead))
            .ReturnsAsync(Authorized(OAuthScopes.GlucoseRead));

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
            .Setup(a => a.AuthorizeTokenAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync((HubAuthorization?)null);

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
            .Setup(a => a.AuthorizeTokenAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync((HubAuthorization?)null);

        await hub.Authorize(new AuthorizeRequest { Token = "tok" });

        // The tenant handed to the authorizer is the connection's resolved tenant — a token
        // from another tenant can never be checked against anything else.
        authorizer.Verify(
            a => a.AuthorizeTokenAsync("tok", Tenant, OAuthScopes.GlucoseRead),
            Times.Once);
    }

    [Fact]
    public async Task Authorize_reports_write_only_when_the_credential_can_write()
    {
        var (hub, _, authorizer) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(Authorized(OAuthScopes.GlucoseRead));

        var result = await hub.Authorize(new AuthorizeRequest { Token = "tok" });

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        json.Should().Contain("\"read\":true");
        json.Should().Contain("\"write\":false");
    }

    [Fact]
    public async Task Subscribe_joins_only_the_categories_the_credential_is_scoped_for()
    {
        var (hub, groups, authorizer) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(Authorized(OAuthScopes.GlucoseRead));
        await hub.Authorize(new AuthorizeRequest { Token = "tok" });

        var result = await hub.Subscribe(new StorageSubscribeRequest
        {
            Collections = ["entries", "glucose", "treatments", "care", "devicestatus"],
        });

        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, $"{Tenant}:entries", It.IsAny<CancellationToken>()),
            Times.Once);
        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, $"{Tenant}:glucose", It.IsAny<CancellationToken>()),
            Times.Once);
        foreach (var denied in new[] { "treatments", "care", "devicestatus" })
        {
            groups.Verify(
                g => g.AddToGroupAsync(ConnectionId, $"{Tenant}:{denied}", It.IsAny<CancellationToken>()),
                Times.Never);
        }

        System.Text.Json.JsonSerializer.Serialize(result)
            .Should().Contain("[\"entries\",\"glucose\"]");
    }

    [Fact]
    public async Task Subscribe_never_joins_an_unclassified_collection()
    {
        var (hub, groups, authorizer) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(Authorized(OAuthScopes.FullAccess));
        await hub.Authorize(new AuthorizeRequest { Token = "tok" });

        await hub.Subscribe(new StorageSubscribeRequest { Collections = ["subjects"] });

        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), $"{Tenant}:subjects", It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
