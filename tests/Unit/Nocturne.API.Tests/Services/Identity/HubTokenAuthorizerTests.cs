using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Covers the in-band hub token authorization used by <c>DataHub.Authorize</c>: OAuth JWTs (the
/// desktop Companion's device-flow tokens) must be validated, tenant-pinned to the connection, and
/// scope-checked; non-JWT tokens fall back to the legacy opaque access-token lookup.
/// </summary>
[Trait("Category", "Unit")]
public class HubTokenAuthorizerTests
{
    // Segment content is irrelevant — the authorizer only counts dots to route to the JWT path.
    private const string JwtShapedToken = "eyJhbGciOi.eyJzdWIiOi.c2ln";
    private const string LegacyToken = "subject-abc123def456";

    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<IOAuthTokenRevocationCache> _revocationCache = new();
    private readonly Mock<IOAuthGrantService> _grantService = new();
    private readonly Mock<IAuthorizationService> _authorizationService = new();

    private HubTokenAuthorizer CreateAuthorizer() => new(
        _jwtService.Object,
        _revocationCache.Object,
        _grantService.Object,
        _authorizationService.Object,
        NullLogger<HubTokenAuthorizer>.Instance);

    private void SetupValidJwt(Guid? tenantId, params string[] scopes) =>
        SetupValidJwt(tenantId, grantId: null, scopes);

    private void SetupValidJwt(Guid? tenantId, Guid? grantId, params string[] scopes)
    {
        _jwtService
            .Setup(s => s.ValidateAccessToken(JwtShapedToken))
            .Returns(JwtValidationResult.Success(new JwtClaims
            {
                SubjectId = Guid.NewGuid(),
                TenantId = tenantId,
                GrantId = grantId,
                Scopes = [.. scopes],
                JwtId = "jti-1",
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }));
        _revocationCache
            .Setup(c => c.IsRevokedAsync(It.IsAny<string>()))
            .ReturnsAsync(false);
    }

    [Fact]
    public async Task Jwt_pinned_to_connection_tenant_with_required_scope_is_authorized()
    {
        SetupValidJwt(Tenant, OAuthScopes.GlucoseRead, OAuthScopes.DeviceNotify);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeTrue();
        // The JWT path must not fall through to the legacy hash lookup.
        _authorizationService.Verify(
            s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Jwt_from_another_tenant_is_rejected()
    {
        SetupValidJwt(OtherTenant, OAuthScopes.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Unpinned_jwt_is_rejected()
    {
        SetupValidJwt(tenantId: null, OAuthScopes.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Jwt_without_required_scope_is_rejected()
    {
        SetupValidJwt(Tenant, OAuthScopes.TherapyRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Jwt_whose_grant_is_revoked_is_rejected()
    {
        var grantId = Guid.CreateVersion7();
        SetupValidJwt(Tenant, grantId, OAuthScopes.GlucoseRead);
        _grantService
            .Setup(g => g.IsGrantRevokedAsync(grantId, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Jwt_with_readwrite_scope_satisfies_read_requirement()
    {
        SetupValidJwt(Tenant, OAuthScopes.GlucoseReadWrite);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Revoked_jwt_is_rejected()
    {
        SetupValidJwt(Tenant, OAuthScopes.GlucoseRead);
        _revocationCache.Setup(c => c.IsRevokedAsync("jti-1")).ReturnsAsync(true);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_jwt_is_rejected_without_legacy_fallback()
    {
        _jwtService
            .Setup(s => s.ValidateAccessToken(JwtShapedToken))
            .Returns(JwtValidationResult.Failure("bad signature", JwtValidationError.InvalidSignature));
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
        _authorizationService.Verify(
            s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Jwt_with_null_connection_tenant_is_rejected()
    {
        SetupValidJwt(Tenant, OAuthScopes.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            JwtShapedToken, connectionTenantId: null, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_opaque_token_uses_hash_lookup()
    {
        _authorizationService
            .Setup(s => s.GenerateJwtFromAccessTokenAsync(LegacyToken))
            .ReturnsAsync(new AuthorizationResponse { Token = "jwt" });
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            LegacyToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeTrue();
        _jwtService.Verify(s => s.ValidateAccessToken(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Unknown_legacy_token_is_rejected()
    {
        _authorizationService
            .Setup(s => s.GenerateJwtFromAccessTokenAsync(LegacyToken))
            .ReturnsAsync((AuthorizationResponse?)null);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.IsTokenAuthorizedAsync(
            LegacyToken, Tenant, OAuthScopes.GlucoseRead);

        result.Should().BeFalse();
    }
}
