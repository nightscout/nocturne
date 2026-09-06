using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The chain exists so the transports cannot drift apart on what makes a bearer JWT usable. That
/// only holds while each link is pinned: the overview hub lost its grant-revocation check once
/// already, and nothing failed.
/// </summary>
[Trait("Category", "Unit")]
public class JwtCredentialValidatorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid GrantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    private readonly Mock<IJwtService> _jwt = new();
    private readonly Mock<IOAuthGrantService> _grants = new();
    private readonly Mock<IOAuthTokenRevocationCache> _revocation = new();

    private const string Token = "aaa.bbb.ccc";

    private JwtCredentialValidator CreateValidator()
    {
        _grants
            .Setup(g => g.IsGrantRevokedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(false);
        _revocation
            .Setup(r => r.IsRevokedAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        return new JwtCredentialValidator(
            _jwt.Object, _grants.Object, _revocation.Object,
            NullLogger<JwtCredentialValidator>.Instance);
    }

    private void TokenValidatesAs(Guid? tenantId, Guid? grantId, string? jwtId = "jti-1")
    {
        _jwt.Setup(j => j.ValidateAccessToken(Token))
            .Returns(JwtValidationResult.Success(new JwtClaims
            {
                SubjectId = SubjectId,
                Scopes = [Scope.GlucoseRead],
                TenantId = tenantId,
                GrantId = grantId,
                JwtId = jwtId,
            }));
    }

    [Fact]
    public async Task A_live_token_is_accepted_and_carries_its_claims()
    {
        TokenValidatesAs(TenantId, GrantId);

        var result = await CreateValidator().ValidateAsync(Token);

        result.IsValid.Should().BeTrue();
        result.Claims!.SubjectId.Should().Be(SubjectId);
        result.Rejection.Should().BeNull();
    }

    [Fact]
    public async Task A_token_that_fails_signature_validation_is_refused_as_invalid()
    {
        _jwt.Setup(j => j.ValidateAccessToken(Token))
            .Returns(JwtValidationResult.Failure("bad signature", JwtValidationError.InvalidSignature));

        var result = await CreateValidator().ValidateAsync(Token);

        result.IsValid.Should().BeFalse();
        result.Rejection.Should().Be(JwtCredentialRejection.Invalid);
    }

    /// <summary>
    /// Disconnecting a connected app can only reach its outstanding access tokens through the grant
    /// id they carry, because the tokens themselves are stateless.
    /// </summary>
    [Fact]
    public async Task A_token_whose_grant_has_been_revoked_is_refused()
    {
        TokenValidatesAs(TenantId, GrantId);
        var validator = CreateValidator();
        _grants.Setup(g => g.IsGrantRevokedAsync(GrantId, TenantId)).ReturnsAsync(true);

        var result = await validator.ValidateAsync(Token);

        result.IsValid.Should().BeFalse("a revoked grant must not keep authorizing its tokens");
        result.Rejection.Should().Be(JwtCredentialRejection.Revoked);
    }

    /// <summary>
    /// A grant-bound token is always minted with its grant's tenant pin, so one arriving without a
    /// pin cannot have its grant looked up — and therefore cannot be shown to still be live.
    /// </summary>
    [Fact]
    public async Task A_grant_bound_token_without_a_tenant_pin_is_refused()
    {
        TokenValidatesAs(tenantId: null, grantId: GrantId);

        var result = await CreateValidator().ValidateAsync(Token);

        result.IsValid.Should().BeFalse("its grant cannot be checked, so it cannot be trusted");
        result.Rejection.Should().Be(JwtCredentialRejection.Revoked);
    }

    [Fact]
    public async Task A_grant_bound_token_without_a_tenant_pin_is_refused_without_a_grant_lookup()
    {
        TokenValidatesAs(tenantId: null, grantId: GrantId);
        var validator = CreateValidator();

        await validator.ValidateAsync(Token);

        _grants.Verify(
            g => g.IsGrantRevokedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never,
            "there is no tenant to look the grant up on");
    }

    [Fact]
    public async Task A_token_in_the_revocation_cache_is_refused()
    {
        TokenValidatesAs(TenantId, grantId: null);
        var validator = CreateValidator();
        _revocation.Setup(r => r.IsRevokedAsync("jti-1")).ReturnsAsync(true);

        var result = await validator.ValidateAsync(Token);

        result.IsValid.Should().BeFalse();
        result.Rejection.Should().Be(JwtCredentialRejection.Revoked);
    }

    /// <summary>
    /// A token with no grant behind it is the ordinary subject-scoped case and must not be refused
    /// for want of one.
    /// </summary>
    [Fact]
    public async Task A_token_with_no_grant_is_accepted_without_a_grant_lookup()
    {
        TokenValidatesAs(TenantId, grantId: null);
        var validator = CreateValidator();

        var result = await validator.ValidateAsync(Token);

        result.IsValid.Should().BeTrue();
        _grants.Verify(
            g => g.IsGrantRevokedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// The tenant pin is the caller's decision, not the chain's — the transports disagree on it by
    /// design, so a pinned token must survive validation and leave the pin to be judged upstream.
    /// </summary>
    [Fact]
    public async Task The_tenant_pin_is_left_for_the_caller_to_judge()
    {
        TokenValidatesAs(TenantId, grantId: null);

        var result = await CreateValidator().ValidateAsync(Token);

        result.IsValid.Should().BeTrue();
        result.Claims!.TenantId.Should().Be(TenantId,
            "the caller applies its own pin policy against this");
    }

    [Fact]
    public async Task A_token_with_no_jti_is_not_looked_up_in_the_revocation_cache()
    {
        TokenValidatesAs(TenantId, grantId: null, jwtId: null);
        var validator = CreateValidator();

        var result = await validator.ValidateAsync(Token);

        result.IsValid.Should().BeTrue();
        _revocation.Verify(r => r.IsRevokedAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Both downstream calls accept a token and a caller can abandon a request mid-chain; the
    /// forwarding is otherwise invisible, since a mock matching any token cannot tell whether one
    /// was passed.
    /// </summary>
    [Fact]
    public async Task The_caller_s_cancellation_token_reaches_both_downstream_lookups()
    {
        TokenValidatesAs(TenantId, GrantId);
        var validator = CreateValidator();
        using var cts = new CancellationTokenSource();

        await validator.ValidateAsync(Token, cts.Token);

        _grants.Verify(
            g => g.IsGrantRevokedAsync(GrantId, TenantId, cts.Token), Times.Once);
        _revocation.Verify(
            r => r.IsRevokedAsync("jti-1", cts.Token), Times.Once);
    }
}
