using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers the in-band Authorize wiring on <see cref="OverviewHub"/>: the subject is resolved
/// from the upgrade request's session AuthContext or an OAuth JWT (never a tenant pin), tenant
/// membership filtering is delegated to <see cref="ITenantOverviewService.GetGlucoseReadTenantsAsync"/>,
/// and the connection joins one "{tenantId}:overview" group per qualifying tenant.
/// </summary>
[Trait("Category", "Unit")]
public class OverviewHubAuthorizeTests
{
    private static readonly Guid Subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string ConnectionId = "conn-1";

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static GlucoseReadTenant Tenant(Guid id) =>
        new(
            new TenantEntity { Id = id, Slug = id.ToString("N")[..8], DisplayName = "T", IsActive = true },
            new HashSet<string> { Scope.GlucoseRead });

    private static (OverviewHub hub,
        Mock<IGroupManager> groups,
        Mock<ITenantOverviewService> overview,
        Mock<IJwtService> jwt,
        Mock<IOAuthTokenRevocationCache> revocation,
        Mock<IOAuthGrantService> grants,
        DefaultHttpContext httpContext) CreateHub()
    {
        var overview = new Mock<ITenantOverviewService>();
        var jwt = new Mock<IJwtService>();
        var revocation = new Mock<IOAuthTokenRevocationCache>();
        var grants = new Mock<IOAuthGrantService>();

        // The real validator, not a stub: these tests are the coverage for the hub's credential
        // chain, and stubbing it would stop them seeing the grant-revocation check the hub used to
        // skip. Unrevoked is the default so existing cases read unchanged.
        grants
            .Setup(g => g.IsGrantRevokedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(false);

        var validator = new JwtCredentialValidator(
            jwt.Object, grants.Object, revocation.Object,
            NullLogger<JwtCredentialValidator>.Instance);

        var hub = new OverviewHub(
            NullLogger<OverviewHub>.Instance, overview.Object, validator);

        var httpContext = new DefaultHttpContext();

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns(ConnectionId);
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);

        var groups = new Mock<IGroupManager>();

        hub.Context = callerContext.Object;
        hub.Groups = groups.Object;
        return (hub, groups, overview, jwt, revocation, grants, httpContext);
    }

    private static JwtValidationResult ValidJwt(
        Guid subjectId, params string[] scopes) =>
        JwtValidationResult.Success(new JwtClaims
        {
            SubjectId = subjectId,
            Scopes = scopes.ToList(),
            JwtId = "jti-1",
        });

    // A structurally JWT-shaped token (three segments) so the hub takes the JWT path.
    private const string JwtShapedToken = "aaa.bbb.ccc";

    [Fact]
    public async Task Authorize_with_session_authcontext_joins_per_tenant_overview_groups()
    {
        var (hub, groups, overview, _, _, _, httpContext) = CreateHub();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Subject,
            AuthType = AuthType.SessionCookie,
        };
        var grantedScopes = new HashSet<string> { Scope.GlucoseRead };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)grantedScopes;

        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Tenant(TenantA), Tenant(TenantB) });

        var result = await hub.Authorize(new OverviewAuthorizeRequest());

        result.Success.Should().BeTrue();
        result.TenantIds.Should().BeEquivalentTo(new[] { TenantA, TenantB });
        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, $"{TenantA}:overview", It.IsAny<CancellationToken>()),
            Times.Once);
        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, $"{TenantB}:overview", It.IsAny<CancellationToken>()),
            Times.Once);
        // The token scopes handed to the seam are the request's granted scopes.
        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(
                Subject,
                It.Is<IReadOnlySet<string>>(s => s.SetEquals(grantedScopes)),
                It.IsAny<AuthType>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Authorize_with_session_authcontext_passes_the_credential_type_to_the_seam()
    {
        var (hub, _, overview, _, _, _, httpContext) = CreateHub();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Subject,
            AuthType = AuthType.SessionCookie,
        };
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GlucoseReadTenant>());

        await hub.Authorize(new OverviewAuthorizeRequest());

        // The seam needs the credential type to know whether the presented scopes are a ceiling.
        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(
                Subject, It.IsAny<IReadOnlySet<string>>(),
                AuthType.SessionCookie, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Authorize_with_jwt_payload_passes_the_oauth_credential_type_to_the_seam()
    {
        var (hub, _, overview, jwt, revocation, _, _) = CreateHub();
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken))
            .Returns(ValidJwt(Subject, Scope.GlucoseRead));
        revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(false);
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GlucoseReadTenant>());

        await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(
                Subject, It.IsAny<IReadOnlySet<string>>(),
                AuthType.OAuthAccessToken, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Authorize_normalizes_the_jwt_scopes_before_handing_them_to_the_seam()
    {
        // The seam treats these as the credential's ceiling, so they have to arrive expanded the way
        // every other JWT-claims consumer expands them: the health.read alias becomes the concrete
        // scopes it stands for, and an unrecognized scope is dropped.
        var (hub, _, overview, jwt, revocation, _, _) = CreateHub();
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken))
            .Returns(ValidJwt(Subject, Scope.HealthRead, "not.a.scope"));
        revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(false);
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GlucoseReadTenant>());

        await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(
                Subject,
                It.Is<IReadOnlySet<string>>(s =>
                    s.IsSupersetOf(Scope.HealthReadExpansion)
                    && !s.Contains(Scope.HealthRead)
                    && !s.Contains("not.a.scope")),
                It.IsAny<AuthType>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The hub is reachable on tenant subdomains, so a credential that only ever authorized one
    // tenant can reach Authorize with a valid AuthContext. Joining the subject's every
    // glucose-readable tenant would widen it, so only subject-scoped credential types are accepted.

    [Theory]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.Guest)]
    [InlineData(AuthType.PlatformAccess)]
    [InlineData(AuthType.InstanceKey)]
    public async Task Authorize_with_tenant_bound_credential_type_is_rejected(AuthType authType)
    {
        var (hub, groups, overview, _, _, _, httpContext) = CreateHub();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Subject,
            AuthType = authType,
        };
        httpContext.Items["GrantedScopes"] =
            (IReadOnlySet<string>)new HashSet<string> { Scope.FullAccess };
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Tenant(TenantA), Tenant(TenantB) });

        var result = await hub.Authorize(new OverviewAuthorizeRequest());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("bound to a single tenant");
        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.OidcToken)]
    [InlineData(AuthType.LegacyJwt)]
    [InlineData(AuthType.LegacyAccessToken)]
    public async Task Authorize_with_subject_scoped_credential_type_on_a_tenant_host_is_accepted(
        AuthType authType)
    {
        // A member browsing a tenant subdomain has a resolved TenantContext on the upgrade
        // request; the guard keys on the credential type, not on a resolved tenant, so their
        // ordinary session still opens the cross-tenant overview.
        var (hub, groups, overview, _, _, _, httpContext) = CreateHub();
        httpContext.Items["TenantContext"] =
            new TenantContext(TenantA, "tenant-a", "Tenant A", true, false);
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Subject,
            AuthType = authType,
        };
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Tenant(TenantA), Tenant(TenantB) });

        var result = await hub.Authorize(new OverviewAuthorizeRequest());

        result.Success.Should().BeTrue();
        result.TenantIds.Should().BeEquivalentTo(new[] { TenantA, TenantB });
        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, $"{TenantB}:overview", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Authorize_with_valid_jwt_joins_groups_using_subject_and_scopes_from_claims()
    {
        var (hub, groups, overview, jwt, revocation, _, _) = CreateHub();
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken))
            .Returns(ValidJwt(Subject, Scope.GlucoseRead));
        revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(false);
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Tenant(TenantA) });

        var result = await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        result.Success.Should().BeTrue();
        result.TenantIds.Should().BeEquivalentTo(new[] { TenantA });
        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, $"{TenantA}:overview", It.IsAny<CancellationToken>()),
            Times.Once);
        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(
                Subject,
                It.Is<IReadOnlySet<string>>(s => s.Contains(Scope.GlucoseRead)),
                It.IsAny<AuthType>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Authorize_with_revoked_jwt_is_rejected()
    {
        var (hub, groups, _, jwt, revocation, _, _) = CreateHub();
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken))
            .Returns(ValidJwt(Subject, Scope.GlucoseRead));
        revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(true);

        var result = await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        result.Success.Should().BeFalse();
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Authorize_with_invalid_jwt_is_rejected()
    {
        var (hub, groups, _, jwt, _, _, _) = CreateHub();
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken))
            .Returns(JwtValidationResult.Failure("expired", JwtValidationError.Expired));

        var result = await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        result.Success.Should().BeFalse();
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Authorize_with_opaque_token_is_rejected_without_jwt_validation()
    {
        var (hub, groups, _, jwt, _, _, _) = CreateHub();

        var result = await hub.Authorize(new OverviewAuthorizeRequest { Token = "opaque-legacy-token" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("cannot be authenticated in-band");
        jwt.Verify(j => j.ValidateAccessToken(It.IsAny<string>()), Times.Never);
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Authorize_without_session_or_token_is_rejected()
    {
        var (hub, groups, _, _, _, _, _) = CreateHub();

        var result = await hub.Authorize(new OverviewAuthorizeRequest());

        result.Success.Should().BeFalse();
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Authorize_with_no_qualifying_tenants_succeeds_with_empty_list_and_no_groups()
    {
        var (hub, groups, overview, jwt, revocation, _, _) = CreateHub();
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken))
            .Returns(ValidJwt(Subject /* no glucose scope */));
        revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(false);
        overview
            .Setup(o => o.GetGlucoseReadTenantsAsync(Subject, It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GlucoseReadTenant>());

        var result = await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        result.Success.Should().BeTrue();
        result.TenantIds.Should().BeEmpty();
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Authorize_with_tenant_pinned_jwt_is_rejected()
    {
        var (hub, groups, overview, jwt, revocation, _, _) = CreateHub();
        var validation = ValidJwt(Subject, Scope.GlucoseRead);
        validation.Claims!.TenantId = TenantA;
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken)).Returns(validation);
        revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(false);

        var result = await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Tenant-pinned");
        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Authorize_with_unauthenticated_authcontext_carrying_subject_is_rejected()
    {
        // The public-share pseudo-context shape: IsAuthenticated = false but a non-null SubjectId.
        var (hub, groups, overview, _, _, _, httpContext) = CreateHub();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = false,
            SubjectId = Subject,
            AuthType = AuthType.None,
        };

        var result = await hub.Authorize(new OverviewAuthorizeRequest());

        result.Success.Should().BeFalse();
        overview.Verify(
            o => o.GetGlucoseReadTenantsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<AuthType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Authorize_with_jwt_lacking_subject_is_rejected()
    {
        var (hub, groups, _, jwt, revocation, _, _) = CreateHub();
        jwt.Setup(j => j.ValidateAccessToken(JwtShapedToken))
            .Returns(ValidJwt(Guid.Empty, Scope.GlucoseRead));
        revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(false);

        var result = await hub.Authorize(new OverviewAuthorizeRequest { Token = JwtShapedToken });

        result.Success.Should().BeFalse();
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
