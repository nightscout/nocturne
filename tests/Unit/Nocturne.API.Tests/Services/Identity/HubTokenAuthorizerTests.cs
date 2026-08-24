using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Covers the in-band hub token authorization used by <c>DataHub.Authorize</c> and
/// <c>AlarmHub.Subscribe</c>: OAuth JWTs (the desktop Companion's device-flow tokens) must be
/// validated, tenant-pinned to the connection, and scope-checked; legacy opaque tokens are pinned to
/// the connection's tenant by an explicit membership row and scoped by that membership;
/// <c>noc_</c> direct grants are read out of a real <c>oauth_grants</c> store, so the tenant the
/// lookup is pinned to is what decides them.
/// </summary>
[Trait("Category", "Unit")]
public class HubTokenAuthorizerTests
{
    // Segment content is irrelevant — the authorizer only counts dots to route to the JWT path.
    private const string JwtShapedToken = "eyJhbGciOi.eyJzdWIiOi.c2ln";
    private const string LegacyToken = "subject-abc123def456";
    private const string ExchangedJwt = "exchanged.jwt.token";
    private const string DirectGrantToken = "noc_abc123def456";

    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid JwtSubject = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<IOAuthTokenRevocationCache> _revocationCache = new();
    private readonly Mock<IOAuthGrantService> _grantService = new();
    private readonly Mock<IAuthorizationService> _authorizationService = new();
    private readonly Mock<ITenantMemberService> _memberService = new();
    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory =
        new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(options =>
                options.UseInMemoryDatabase($"HubTokenAuthorizer_{Guid.NewGuid()}"))
            .BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<NocturneDbContext>>();

    private HubTokenAuthorizer CreateAuthorizer(IConfiguration? configuration = null) => new(
        _jwtService.Object,
        _revocationCache.Object,
        _grantService.Object,
        _authorizationService.Object,
        _memberService.Object,
        _dbContextFactory,
        TimeProvider.System,
        configuration ?? new ConfigurationBuilder().Build(),
        NullLogger<HubTokenAuthorizer>.Instance);

    /// <summary>
    /// A valid JWT for <see cref="JwtSubject"/>, who is seeded as a superuser member of
    /// <see cref="Tenant"/>. Membership is the ceiling the token's scopes are intersected against, so
    /// it is seeded wide here and re-seeded narrower only by the tests about that intersection.
    /// </summary>
    private void SetupValidJwt(Guid? tenantId, params string[] scopes) =>
        SetupValidJwt(tenantId, grantId: null, scopes);

    private void SetupValidJwt(Guid? tenantId, Guid? grantId, params string[] scopes)
    {
        SeedMember(Tenant, JwtSubject, Scope.FullAccess);
        _jwtService
            .Setup(s => s.ValidateAccessToken(JwtShapedToken))
            .Returns(JwtValidationResult.Success(new JwtClaims
            {
                SubjectId = JwtSubject,
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

    /// <summary>
    /// Stubs the legacy subject-token exchange and the validation of the JWT it mints. That exchange
    /// resolves a subject, so the JWT it returns carries permissions and no tenant pin.
    /// </summary>
    private void SetupExchangedToken(Guid subjectId, params string[] permissions)
    {
        _authorizationService
            .Setup(s => s.GenerateJwtFromAccessTokenAsync(LegacyToken))
            .ReturnsAsync(new AuthorizationResponse { Token = ExchangedJwt });
        _jwtService
            .Setup(s => s.ValidateAccessToken(ExchangedJwt))
            .Returns(JwtValidationResult.Success(new JwtClaims
            {
                SubjectId = subjectId,
                TenantId = null,
                Scopes = [],
                Permissions = [.. permissions],
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }));
    }

    /// <summary>Makes <paramref name="subjectId"/> a member of <paramref name="tenantId"/> only.</summary>
    private void SeedMember(Guid tenantId, Guid subjectId, params string[] rolePermissions)
    {
        _memberService
            .Setup(m => m.GetEffectivePermissionsAsync(
                subjectId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid queriedTenant, CancellationToken _) =>
                queriedTenant == tenantId ? rolePermissions.ToHashSet() : null);
    }

    /// <summary>
    /// Writes a <c>noc_</c> direct grant for <see cref="DirectGrantToken"/> into the store, owned by
    /// <paramref name="tenantId"/>. The authorizer reads it back through its own tenant-pinned
    /// lookup, so the row's tenant is what the pin is tested against.
    /// </summary>
    private async Task<Guid> SeedDirectGrantAsync(
        Guid tenantId, Guid subjectId, DateTime? revokedAt = null, params string[] scopes)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.TenantId = tenantId;

        var grant = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = subjectId,
            GrantType = OAuthGrantTypes.Direct,
            TokenHash = DirectGrantTokenHandler.ComputeSha256Hex(DirectGrantToken),
            Scopes = [.. scopes],
            RevokedAt = revokedAt,
        };

        db.OAuthGrants.Add(grant);
        await db.SaveChangesAsync();

        return grant.Id;
    }

    [Fact]
    public async Task Jwt_pinned_to_connection_tenant_with_required_scope_is_authorized()
    {
        SetupValidJwt(Tenant, Scope.GlucoseRead, Scope.DeviceNotify);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().NotBeNull();
        result!.Kind.Should().Be(HubCredentialKind.Subject);
        // The JWT path must not fall through to the legacy hash lookup.
        _authorizationService.Verify(
            s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Jwt_from_another_tenant_is_rejected()
    {
        SetupValidJwt(OtherTenant, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Unpinned_jwt_is_rejected()
    {
        SetupValidJwt(tenantId: null, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Jwt_without_required_scope_is_rejected()
    {
        SetupValidJwt(Tenant, Scope.TherapyRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Jwt_whose_grant_is_revoked_is_rejected()
    {
        var grantId = Guid.CreateVersion7();
        SetupValidJwt(Tenant, grantId, Scope.GlucoseRead);
        _grantService
            .Setup(g => g.IsGrantRevokedAsync(grantId, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Jwt_with_readwrite_scope_satisfies_read_requirement()
    {
        SetupValidJwt(Tenant, Scope.GlucoseReadWrite);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Revoked_jwt_is_rejected()
    {
        SetupValidJwt(Tenant, Scope.GlucoseRead);
        _revocationCache.Setup(c => c.IsRevokedAsync("jti-1")).ReturnsAsync(true);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Invalid_jwt_is_rejected_without_legacy_fallback()
    {
        _jwtService
            .Setup(s => s.ValidateAccessToken(JwtShapedToken))
            .Returns(JwtValidationResult.Failure("bad signature", JwtValidationError.InvalidSignature));
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
        _authorizationService.Verify(
            s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Jwt_carrying_a_scope_the_membership_no_longer_grants_is_rejected()
    {
        // A token's scopes are frozen at issue; a membership is not. A member demoted to read-only
        // must lose alert acknowledgement on the hub the moment the demotion lands, exactly as
        // MemberScopeMiddleware makes them lose it over HTTP.
        SetupValidJwt(Tenant, Scope.AlertsReadWrite);
        SeedMember(Tenant, JwtSubject, Scope.GlucoseRead, Scope.AlertsRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.AlertsReadWrite);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Jwt_scopes_are_narrowed_to_what_the_membership_grants()
    {
        // The credential is the ceiling in the other direction too: the authorized connection must
        // not carry a scope the token holds but the membership does not, because the hub freezes
        // this scope set for the life of the connection.
        SetupValidJwt(Tenant, Scope.GlucoseRead, Scope.TreatmentsRead);
        SeedMember(Tenant, JwtSubject, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().NotBeNull();
        result!.Satisfies(Scope.TreatmentsRead).Should().BeFalse();
    }

    [Fact]
    public async Task Jwt_for_a_subject_with_no_membership_on_the_connection_tenant_is_rejected()
    {
        // AuthenticationMiddleware rejects a membership-less OAuth access token outright over HTTP;
        // the hub must not be the one plane where it still authorizes.
        SetupValidJwt(Tenant, Scope.GlucoseRead);
        SeedMember(OtherTenant, JwtSubject, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Jwt_with_null_connection_tenant_is_rejected()
    {
        SetupValidJwt(Tenant, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            JwtShapedToken, connectionTenantId: null, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Legacy_opaque_token_for_a_member_of_the_connection_tenant_is_authorized()
    {
        var subjectId = Guid.CreateVersion7();
        SetupExchangedToken(subjectId);
        SeedMember(Tenant, subjectId, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            LegacyToken, Tenant, Scope.GlucoseRead);

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task Legacy_opaque_token_from_another_tenants_member_is_rejected()
    {
        // The exchange only proves the token exists. Without the membership check a token minted on
        // another tenant would authorize this connection's tenant-scoped groups.
        var subjectId = Guid.CreateVersion7();
        SetupExchangedToken(subjectId);
        SeedMember(OtherTenant, subjectId, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            LegacyToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Legacy_opaque_token_without_the_required_scope_is_rejected()
    {
        var subjectId = Guid.CreateVersion7();
        SetupExchangedToken(subjectId);
        // Membership grants only therapy, so the glucose gate is not satisfied.
        SeedMember(Tenant, subjectId, Scope.TherapyRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            LegacyToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Direct_grant_token_pinned_to_another_tenant_is_rejected()
    {
        // The grant exists and its subject is a member of the connection's tenant; only the grant
        // row's own tenant differs. The lookup is pinned to the connection's tenant, so it finds
        // nothing — a hub connection cannot reach another tenant's grant.
        var subjectId = Guid.CreateVersion7();
        await SeedDirectGrantAsync(OtherTenant, subjectId, revokedAt: null, Scope.GlucoseRead);
        SeedMember(Tenant, subjectId, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            DirectGrantToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
        _memberService.Verify(
            m => m.GetEffectivePermissionsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Direct grants never reach the subject-token exchange, whose grant read is unpinned.
        _authorizationService.Verify(
            s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Direct_grant_token_pinned_to_the_connection_tenant_is_authorized()
    {
        var subjectId = Guid.CreateVersion7();
        await SeedDirectGrantAsync(Tenant, subjectId, revokedAt: null, Scope.GlucoseRead);
        SeedMember(Tenant, subjectId, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            DirectGrantToken, Tenant, Scope.GlucoseRead);

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(Tenant);
        result.Kind.Should().Be(HubCredentialKind.Subject);
        result.SubjectId.Should().Be(subjectId);
        _authorizationService.Verify(
            s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Direct_grant_scopes_are_narrowed_to_what_the_membership_grants()
    {
        // A direct grant is a scoped credential, so membership is the other half of the ceiling
        // exactly as MemberScopeMiddleware applies it to the same credential over HTTP.
        var subjectId = Guid.CreateVersion7();
        await SeedDirectGrantAsync(
            Tenant, subjectId, revokedAt: null, Scope.GlucoseRead, Scope.TreatmentsRead);
        SeedMember(Tenant, subjectId, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            DirectGrantToken, Tenant, Scope.GlucoseRead);

        result.Should().NotBeNull();
        result!.Satisfies(Scope.TreatmentsRead).Should().BeFalse();
    }

    [Fact]
    public async Task Direct_grant_whose_subject_is_not_a_member_is_rejected()
    {
        var subjectId = Guid.CreateVersion7();
        await SeedDirectGrantAsync(Tenant, subjectId, revokedAt: null, Scope.GlucoseRead);
        SeedMember(OtherTenant, subjectId, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            DirectGrantToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Revoked_direct_grant_is_rejected()
    {
        var subjectId = Guid.CreateVersion7();
        await SeedDirectGrantAsync(
            Tenant, subjectId, DateTime.UtcNow.AddMinutes(-1), Scope.GlucoseRead);
        SeedMember(Tenant, subjectId, Scope.GlucoseRead);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            DirectGrantToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_direct_grant_token_is_rejected()
    {
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            DirectGrantToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_legacy_token_is_rejected()
    {
        _authorizationService
            .Setup(s => s.GenerateJwtFromAccessTokenAsync(LegacyToken))
            .ReturnsAsync((AuthorizationResponse?)null);
        var authorizer = CreateAuthorizer();

        var result = await authorizer.AuthorizeTokenAsync(
            LegacyToken, Tenant, Scope.GlucoseRead);

        result.Should().BeNull();
    }

    [Fact]
    public void Instance_key_is_authorized_only_when_the_hash_matches()
    {
        var authorizer = CreateAuthorizer(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INSTANCE_KEY"] = "s3cret" })
            .Build());

        var expected = HashUtils.Sha256Hex("s3cret");

        var granted = authorizer.AuthorizeInstanceKey(expected, Tenant);
        granted.Should().NotBeNull();
        granted!.Scopes.Should().Contain(Scope.FullAccess);
        granted.Kind.Should().Be(HubCredentialKind.Infrastructure);

        authorizer.AuthorizeInstanceKey("deadbeef", Tenant).Should().BeNull();
        authorizer.AuthorizeInstanceKey(expected, connectionTenantId: null).Should().BeNull();
    }

    [Fact]
    public void Instance_key_is_rejected_when_none_is_configured()
    {
        var authorizer = CreateAuthorizer();

        authorizer.AuthorizeInstanceKey(HashUtils.Sha256Hex(""), Tenant).Should().BeNull();
    }
}
