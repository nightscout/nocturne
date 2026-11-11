namespace Nocturne.Core.Models.Authorization;

/// <summary>An authorization code to store, hashed by the caller.</summary>
public sealed record NewAuthorizationCode(
    Guid ClientEntityId,
    Guid SubjectId,
    string CodeHash,
    IReadOnlyList<string> Scopes,
    string RedirectUri,
    string CodeChallenge,
    DateTime ExpiresAt,
    bool LimitTo24Hours);

/// <summary>A stored authorization code with the public <c>client_id</c> of its client.</summary>
/// <param name="ClientId">Null when the client row is gone.</param>
public sealed record AuthorizationCodeRecord(
    Guid Id,
    Guid ClientEntityId,
    string? ClientId,
    Guid SubjectId,
    IReadOnlyList<string> Scopes,
    string RedirectUri,
    string CodeChallenge,
    DateTime ExpiresAt,
    DateTime? RedeemedAt,
    bool LimitTo24Hours);

/// <summary>An OAuth refresh token to store under a grant, hashed by the caller.</summary>
public sealed record NewOAuthRefreshToken(Guid TenantId, Guid GrantId, string TokenHash, DateTime ExpiresAt);

/// <summary>
/// A stored OAuth refresh token with the grant it was issued under. A token is rotated when it is
/// revoked with a <see cref="ReplacedById"/>, and signed out when revoked without one.
/// </summary>
/// <param name="Grant">Null when the grant row is gone.</param>
public sealed record OAuthRefreshTokenRecord(
    Guid Id,
    Guid TenantId,
    Guid GrantId,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    Guid? ReplacedById,
    int GraceRedemptions,
    OAuthRefreshTokenGrant? Grant)
{
    public bool IsRevoked => RevokedAt.HasValue;
}

/// <summary>What a refresh token needs of its grant to mint an access token.</summary>
/// <param name="ClientId">The public <c>client_id</c>; null for a grant with no client row.</param>
public sealed record OAuthRefreshTokenGrant(
    Guid Id,
    Guid TenantId,
    Guid SubjectId,
    IReadOnlyList<string> Scopes,
    string? ClientId,
    bool LimitTo24Hours,
    bool IsRevoked);

/// <summary>A device code to store, hashed by the caller, with its user code already normalized.</summary>
public sealed record NewDeviceCode(
    string ClientId,
    string DeviceCodeHash,
    string UserCode,
    IReadOnlyList<string> Scopes,
    DateTime ExpiresAt,
    int Interval);

/// <summary>A stored device code (RFC 8628).</summary>
public sealed record DeviceCodeRecord(
    Guid Id,
    string ClientId,
    string UserCode,
    IReadOnlyList<string> Scopes,
    DateTime ExpiresAt,
    DateTime? ApprovedAt,
    DateTime? DeniedAt,
    DateTime? RedeemedAt,
    Guid? GrantId,
    Guid? SubjectId,
    int Interval,
    DateTime? LastPolledAt)
{
    public bool IsApproved => ApprovedAt.HasValue;

    public bool IsDenied => DeniedAt.HasValue;
}

/// <summary>How many expired codes of each kind a sweep deleted.</summary>
public sealed record ExpiredCodeDeletion(int DeviceCodes, int AuthorizationCodes);
