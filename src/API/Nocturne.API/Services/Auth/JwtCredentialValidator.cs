using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Validates a bearer JWT down to the point where a transport has to decide for itself.
///
/// Four call sites used to run this chain independently — the HTTP handler, the SignalR hub
/// authorizer, the overview hub's in-band authorize, and token introspection — and they had
/// drifted: the overview hub checked the revocation cache but never asked whether the token's
/// GRANT had been revoked, so disconnecting a connected app left its hub connections authorized
/// until the access token expired.
///
/// The tenant pin is deliberately NOT decided here. The three transports disagree on purpose: an
/// HTTP request accepts an unpinned token because <c>MemberScopeMiddleware</c> re-resolves
/// membership per request, a tenant hub requires the pin to match its connection because nothing
/// re-checks after the handshake, and the overview hub requires the token to be unpinned because
/// it spans every tenant the subject belongs to. Folding those into one rule would erase a
/// distinction each site chose. Callers read <see cref="JwtClaims.TenantId"/> off the result and
/// apply their own.
/// </summary>
public interface IJwtCredentialValidator
{
    /// <summary>
    /// Validates <paramref name="token"/>'s signature and lifetime, then checks that neither the
    /// token nor the grant behind it has been revoked.
    /// </summary>
    Task<JwtCredentialResult> ValidateAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>Why a JWT credential was refused.</summary>
public enum JwtCredentialRejection
{
    /// <summary>Signature, lifetime, issuer or audience did not hold up.</summary>
    Invalid,

    /// <summary>The token, or the grant it was minted against, has been revoked.</summary>
    Revoked,
}

/// <summary>
/// The outcome of <see cref="IJwtCredentialValidator.ValidateAsync"/>. A valid result carries the
/// claims; the caller still owes the tenant-pin decision.
/// </summary>
public sealed record JwtCredentialResult
{
    private JwtCredentialResult() { }

    /// <summary>The validated claims, or <see langword="null"/> when the credential was refused.</summary>
    public JwtClaims? Claims { get; private init; }

    /// <summary>Why it was refused, or <see langword="null"/> when it was not.</summary>
    public JwtCredentialRejection? Rejection { get; private init; }

    /// <summary>Diagnostic detail for logging. Never surfaced to the caller of the API.</summary>
    public string? Error { get; private init; }

    /// <summary>Whether the credential survived validation.</summary>
    public bool IsValid => Claims is not null;

    internal static JwtCredentialResult Valid(JwtClaims claims) => new() { Claims = claims };

    internal static JwtCredentialResult Refused(JwtCredentialRejection rejection, string error) =>
        new() { Rejection = rejection, Error = error };
}

/// <inheritdoc cref="IJwtCredentialValidator"/>
public sealed class JwtCredentialValidator : IJwtCredentialValidator
{
    private readonly IJwtService _jwtService;
    private readonly IOAuthGrantService _grantService;
    private readonly IOAuthTokenRevocationCache _revocationCache;
    private readonly ILogger<JwtCredentialValidator> _logger;

    public JwtCredentialValidator(
        IJwtService jwtService,
        IOAuthGrantService grantService,
        IOAuthTokenRevocationCache revocationCache,
        ILogger<JwtCredentialValidator> logger)
    {
        _jwtService = jwtService;
        _grantService = grantService;
        _revocationCache = revocationCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<JwtCredentialResult> ValidateAsync(
        string token, CancellationToken cancellationToken = default)
    {
        var validation = _jwtService.ValidateAccessToken(token);
        if (!validation.IsValid || validation.Claims is null)
        {
            _logger.LogDebug("JWT validation failed: {Error}", validation.Error);
            return JwtCredentialResult.Refused(
                JwtCredentialRejection.Invalid, validation.Error ?? "Invalid token");
        }

        var claims = validation.Claims;

        if (claims.GrantId.HasValue)
        {
            // A grant-bound token is always minted with its grant's tenant pin. One arriving
            // without a pin cannot have its grant looked up, so it cannot be shown to be live.
            if (!claims.TenantId.HasValue)
            {
                _logger.LogWarning(
                    "Access token carries grant {GrantId} without a tenant pin", claims.GrantId);
                return JwtCredentialResult.Refused(
                    JwtCredentialRejection.Revoked, "Token has been revoked");
            }

            if (await _grantService.IsGrantRevokedAsync(claims.GrantId.Value, claims.TenantId.Value))
            {
                _logger.LogDebug(
                    "Access token's grant has been revoked (grant: {GrantId})", claims.GrantId);
                return JwtCredentialResult.Refused(
                    JwtCredentialRejection.Revoked, "Token has been revoked");
            }
        }

        if (!string.IsNullOrEmpty(claims.JwtId)
            && await _revocationCache.IsRevokedAsync(claims.JwtId))
        {
            _logger.LogDebug("Access token has been revoked (jti: {Jti})", claims.JwtId);
            return JwtCredentialResult.Refused(
                JwtCredentialRejection.Revoked, "Token has been revoked");
        }

        return JwtCredentialResult.Valid(claims);
    }
}
