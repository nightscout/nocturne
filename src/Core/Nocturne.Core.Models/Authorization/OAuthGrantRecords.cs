namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// A grant to store: an app consent, a direct token or a guest link. The tenant is the context's;
/// the hashes are the caller's, already computed.
/// </summary>
public sealed record NewOAuthGrant(
    Guid? ClientEntityId,
    Guid SubjectId,
    string GrantType,
    IReadOnlyList<string> Scopes,
    string? Label,
    string? TokenHash,
    string? LegacySecretHash,
    DateTime? ExpiresAt,
    bool LimitTo24Hours,
    bool ApprovedByDeviceCode = false,
    Guid? CreatedBySubjectId = null);

/// <summary>
/// A credential the instance did not mint, taken on as a direct grant: a classic Nightscout
/// site's master <c>api-secret</c> or one of its per-subject access tokens. At least one of the
/// three hashes is required.
/// </summary>
public sealed record AdoptedLegacyCredential(
    Guid SubjectId,
    string Label,
    IReadOnlyList<string> Scopes,
    string? TokenHash = null,
    string? LegacySecretHash = null,
    string? LegacyTokenDigest = null);

/// <summary>The client a grant was made for.</summary>
public sealed record OAuthGrantClient(string ClientId, string? DisplayName, string? ClientUri, string? LogoUri);

/// <summary>A stored grant. Its credential hashes stay in the store.</summary>
/// <param name="Client">Null for a direct or guest grant, and when the client row is gone.</param>
public sealed record OAuthGrantRecord(
    Guid Id,
    Guid TenantId,
    Guid? ClientEntityId,
    OAuthGrantClient? Client,
    OAuthClientIdentity ClientIdentity,
    Guid SubjectId,
    Guid? CreatedBySubjectId,
    string GrantType,
    IReadOnlyList<string> Scopes,
    string? Label,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    string? LastUsedIp,
    string? LastUsedUserAgent,
    DateTime? RevokedAt,
    DateTime? DismissedAt,
    DateTime? ExpiresAt,
    DateTime? ActivatedAt,
    string? ActivatedIp,
    string? ActivatedUserAgent,
    bool LimitTo24Hours,
    bool IsMigrated)
{
    public bool IsRevoked => RevokedAt.HasValue;

    /// <summary>
    /// Whether this is a direct grant that authenticates at <paramref name="now"/>: not revoked,
    /// and either open-ended or short of its expiry. The expiry instant itself does not
    /// authenticate.
    /// </summary>
    /// <remarks>
    /// The same rule the store applies when it reads live direct grants,
    /// <c>OAuthGrantRepository.IsLiveDirectGrant</c>, so a grant one read refuses is not accepted
    /// by a caller judging a record.
    /// </remarks>
    public bool IsLiveDirectGrantAt(DateTime now) =>
        GrantType == OAuthGrantTypes.Direct && RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>What a grant edit changes; a null member leaves the stored value as it is.</summary>
/// <param name="ApprovedByDeviceCode">
/// True records a device-code approval. The flag is never cleared, so false is not an edit.
/// </param>
public sealed record OAuthGrantEdit(
    string? Label = null,
    IReadOnlyList<string>? Scopes = null,
    bool? LimitTo24Hours = null,
    bool ApprovedByDeviceCode = false);

/// <summary>Where and by what a grant was presented.</summary>
public sealed record GrantPresentation(string? IpAddress, string? UserAgent);

/// <summary>What decides whether a token minted from a grant still authorizes, and what it carries.</summary>
public sealed record OAuthGrantState(
    DateTime? RevokedAt,
    DateTime? ExpiresAt,
    IReadOnlyList<string> Scopes,
    bool LimitTo24Hours);

/// <summary>
/// The credential a direct grant is looked up by. A hash is compared whole; a digest by prefix,
/// which is how the Nightscout instance that issued the token matched it.
/// </summary>
public abstract record DirectGrantLookup
{
    private DirectGrantLookup()
    {
    }

    /// <summary>A <c>noc_</c> token by the SHA-256 of any of its spellings.</summary>
    public sealed record ByTokenHash(IReadOnlyList<string> Hashes) : DirectGrantLookup;

    /// <summary>A legacy <c>api-secret</c> by the SHA-1 the client sends.</summary>
    public sealed record ByLegacySecretHash(string Hash) : DirectGrantLookup;

    /// <summary>An imported Nightscout token by a validated hex prefix of its digest.</summary>
    public sealed record ByLegacyTokenDigestPrefix(string Prefix) : DirectGrantLookup;
}

/// <summary>
/// What a request authenticated by a direct grant needs of it. The hashes are what the
/// credential's audit fingerprint is taken of.
/// </summary>
public sealed record DirectGrantCredential(
    Guid Id,
    Guid SubjectId,
    IReadOnlyList<string> Scopes,
    string? Label,
    bool LimitTo24Hours,
    DateTime? LastUsedAt,
    bool IsMigrated,
    string? TokenHash,
    string? LegacySecretHash);
