using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Models.OAuth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// The introspection endpoint (<c>POST /api/oauth/introspect</c>) resolves a token to its subject
/// id, client and scopes. RFC 7662 section 2.1 requires the endpoint to authenticate its caller;
/// every client on this server is public, so the caller authenticates as a subject the way the
/// other credential-bearing endpoints on the controller do, and the answer is bounded by that
/// identity.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthControllerIntrospectTests
{
    private const string Token = "header.payload.signature";

    private readonly Guid _callerSubjectId = Guid.CreateVersion7();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<IOAuthGrantService> _grantService = new();
    private readonly Mock<IOAuthTokenRevocationCache> _revocationCache = new();

    /// <summary>A token that validates, is not revoked, and belongs to <paramref name="subjectId"/>.</summary>
    private void TokenBelongsTo(Guid subjectId)
    {
        _jwtService
            .Setup(s => s.ValidateAccessToken(Token))
            .Returns(JwtValidationResult.Success(new JwtClaims
            {
                SubjectId = subjectId,
                TenantId = _tenantId,
                ClientId = "test-client-id",
                JwtId = "test-jwt-id",
                Scopes = [OAuthScopes.GlucoseRead],
                IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }));
        _revocationCache
            .Setup(c => c.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _grantService
            .Setup(g => g.IsGrantRevokedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
    }

    private OAuthController CreateController(AuthContext? authContext)
    {
        var httpContext = new DefaultHttpContext();
        if (authContext != null)
        {
            httpContext.Items["AuthContext"] = authContext;
        }

        return new OAuthController(
            Mock.Of<IOAuthClientService>(),
            _grantService.Object,
            Mock.Of<IOAuthTokenService>(),
            Mock.Of<IOAuthDeviceCodeService>(),
            Mock.Of<ISubjectService>(),
            _jwtService.Object,
            _revocationCache.Object,
            NullLogger<OAuthController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private OAuthController AuthenticatedAs(Guid subjectId) => CreateController(new AuthContext
    {
        IsAuthenticated = true,
        AuthType = AuthType.SessionCookie,
        SubjectId = subjectId,
        TenantId = _tenantId,
    });

    private static TokenIntrospectionResponse ResponseOf(
        ActionResult<TokenIntrospectionResponse> result) =>
        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TokenIntrospectionResponse>().Subject;

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        TokenBelongsTo(_callerSubjectId);

        var result = await CreateController(authContext: null).Introspect(Token);

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.Value.Should().BeOfType<OAuthError>()
            .Which.Error.Should().Be("access_denied");
    }

    /// <summary>
    /// The public share subject is carried as an unauthenticated context with a subject id
    /// attached, so the gate has to read the flag rather than the presence of an id.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_caller_carrying_a_subject_id_is_refused()
    {
        TokenBelongsTo(_callerSubjectId);

        var result = await CreateController(new AuthContext
        {
            IsAuthenticated = false,
            AuthType = AuthType.None,
            SubjectId = Guid.CreateVersion7(),
            TenantId = _tenantId,
        }).Introspect(Token);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task An_unauthenticated_caller_learns_nothing_about_the_token()
    {
        TokenBelongsTo(_callerSubjectId);

        var result = await CreateController(authContext: null).Introspect(Token);

        result.Result.Should().NotBeOfType<OkObjectResult>();
        result.Value.Should().BeNull();
        _jwtService.Verify(s => s.ValidateAccessToken(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A guest session authenticates as a principal with no subject of its own — it acts as the
    /// data owner via <see cref="AuthContext.ActingAsSubjectId"/>. Introspection resolves the
    /// caller's own <see cref="AuthContext.SubjectId"/>, never the acting-as owner, so a guest is
    /// refused rather than handed the owner's token metadata. Instance-key and dev-auth admins are
    /// the other subjectless-but-authenticated principals this same guard covers.
    /// </summary>
    [Fact]
    public async Task A_subjectless_guest_principal_is_refused_and_no_token_is_resolved()
    {
        var dataOwnerSubjectId = Guid.CreateVersion7();
        TokenBelongsTo(dataOwnerSubjectId);

        var result = await CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.Guest,
            SubjectId = null,
            ActingAsSubjectId = dataOwnerSubjectId,
            TenantId = _tenantId,
        }).Introspect(Token);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        _jwtService.Verify(s => s.ValidateAccessToken(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task An_authenticated_caller_introspecting_its_own_token_gets_the_metadata()
    {
        TokenBelongsTo(_callerSubjectId);

        var result = await AuthenticatedAs(_callerSubjectId).Introspect(Token);

        var response = ResponseOf(result);
        response.Active.Should().BeTrue();
        response.Sub.Should().Be(_callerSubjectId.ToString());
        response.Scope.Should().Be(OAuthScopes.GlucoseRead);
        response.TokenType.Should().Be("access_token");
    }

    [Fact]
    public async Task Another_subjects_token_reads_as_inactive()
    {
        TokenBelongsTo(Guid.CreateVersion7());

        var result = await AuthenticatedAs(_callerSubjectId).Introspect(Token);

        var response = ResponseOf(result);
        response.Active.Should().BeFalse();
        response.Sub.Should().BeNull();
        response.Scope.Should().BeNull();
        response.ClientId.Should().BeNull();
    }

    /// <summary>
    /// Subjects are global across tenants, so a tenant-pinned token is bound to the caller's tenant
    /// as well as its subject: the same subject, authenticated on tenant A, must not resolve its
    /// tenant-B token here.
    /// </summary>
    [Fact]
    public async Task The_callers_own_token_pinned_to_another_tenant_reads_as_inactive()
    {
        // Same subject as the caller, but the token is pinned to a different tenant.
        var otherTenantId = Guid.CreateVersion7();
        _jwtService
            .Setup(s => s.ValidateAccessToken(Token))
            .Returns(JwtValidationResult.Success(new JwtClaims
            {
                SubjectId = _callerSubjectId,
                TenantId = otherTenantId,
                ClientId = "test-client-id",
                JwtId = "test-jwt-id",
                Scopes = [OAuthScopes.GlucoseRead],
                IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }));
        _revocationCache
            .Setup(c => c.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _grantService
            .Setup(g => g.IsGrantRevokedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await AuthenticatedAs(_callerSubjectId).Introspect(Token);

        var response = ResponseOf(result);
        response.Active.Should().BeFalse();
        response.Sub.Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_token_of_the_callers_own_reads_as_inactive()
    {
        TokenBelongsTo(_callerSubjectId);
        _revocationCache
            .Setup(c => c.IsRevokedAsync("test-jwt-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await AuthenticatedAs(_callerSubjectId).Introspect(Token);

        ResponseOf(result).Active.Should().BeFalse();
    }
}
