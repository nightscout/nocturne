using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing OAuth authorization grants.
/// </summary>
/// <seealso cref="IOAuthClientService"/>
/// <seealso cref="IOAuthTokenService"/>
public interface IOAuthGrantService
{
    /// <summary>
    /// Create a new grant (user approved app for scopes).
    /// If an active grant already exists for this client+subject, it is updated with the new scopes.
    /// </summary>
    /// <param name="subjectId">The subject (user) ID who is granting access</param>
    /// <param name="scopes">The scopes being granted</param>
    /// <param name="limitTo24Hours">
    /// Whether tokens minted from the grant may read only the last 24 hours. The latest consent
    /// sets it.
    /// </param>
    /// <returns>The grant as written; null when the subject holds no membership of the tenant, so nothing was written.</returns>
    Task<OAuthGrantInfo?> CreateOrUpdateGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        bool limitTo24Hours = false,
        CancellationToken ct = default
    );

    /// <summary>
    /// Get an active (non-revoked) grant for a client+subject combination.
    /// </summary>
    Task<OAuthGrantInfo?> GetActiveGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Get all active grants for a subject (for the management UI).
    /// </summary>
    Task<IReadOnlyList<OAuthGrantInfo>> GetGrantsForSubjectAsync(
        Guid subjectId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Get the active (non-revoked) grant with this id that this subject owns. A grant that is
    /// revoked, absent, or owned by another subject all come back <c>null</c>, so a caller
    /// authorizing against one cannot tell the three apart.
    /// </summary>
    /// <param name="grantId">The grant ID to look up</param>
    /// <param name="ownerSubjectId">The owner subject ID (for authorization check)</param>
    Task<OAuthGrantInfo?> GetGrantForSubjectAsync(
        Guid grantId,
        Guid ownerSubjectId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Revoke a grant (soft delete). Invalidates all associated refresh tokens, and — because
    /// access tokens carry their grant id — every outstanding access token minted from the grant.
    /// </summary>
    Task RevokeGrantAsync(Guid grantId, CancellationToken ct = default);

    /// <summary>
    /// Whether the grant still authorizes, for authenticating an access token that carries a grant
    /// id: not revoked and not past its expiry. A grant that cannot be found is not live.
    /// </summary>
    /// <param name="grantId">The grant id carried by the access token.</param>
    /// <param name="tenantId">The tenant the presented token is pinned to, already verified against
    /// the request's tenant by the caller. Used to scope the lookup.</param>
    Task<bool> IsGrantLiveAsync(Guid grantId, Guid tenantId, CancellationToken ct = default);

    Task UpdateLastUsedAsync(
        Guid grantId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default
    );

    /// <summary>
    /// Update grant label and/or scopes. Returns null if the grant is not found, is not owned, or is
    /// revoked, as <see cref="GetGrantForSubjectAsync"/> does.
    /// </summary>
    /// <param name="ownerSubjectId">The owner subject ID (for authorization check)</param>
    /// <param name="bounds">What bounds new scopes, by the kind of grant.</param>
    /// <exception cref="GrantCeilingViolationException">New scopes exceed <paramref name="bounds"/>.</exception>
    Task<OAuthGrantInfo?> UpdateGrantAsync(
        Guid grantId,
        Guid ownerSubjectId,
        GrantScopeBounds bounds,
        string? label = null,
        IEnumerable<string>? scopes = null,
        CancellationToken ct = default
    );
}

/// <summary>
/// What bounds the new scopes of a grant its owner edits. An app grant is the owner's own credential,
/// bounded by <paramref name="OwnCredential"/>, everything the owner holds. A guest link gives another
/// person access, so it is also bounded by <paramref name="GivenToOthers"/>, what the owner may give
/// anyone else; null when they may give nothing, which refuses new scopes on a guest link.
/// </summary>
public sealed record GrantScopeBounds(IReadOnlySet<string> OwnCredential, IReadOnlySet<string>? GivenToOthers);

public class OAuthGrantInfo
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Internal entity ID of the OAuth client, or null for follower grants without a registered client.</summary>
    public Guid? ClientEntityId { get; set; }

    public string ClientId { get; set; } = string.Empty;

    /// <summary>Display name of the client shown on the consent screen, if any.</summary>
    public string? ClientDisplayName { get; set; }

    /// <summary>Homepage URI of the client, if any.</summary>
    public string? ClientUri { get; set; }

    /// <summary>Logo URI of the client, if any.</summary>
    public string? LogoUri { get; set; }

    /// <summary>How far the grant's tokens are known to belong to the app its client names.</summary>
    public OAuthClientIdentity ClientIdentity { get; set; }

    /// <summary>The subject (user) who granted access.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Grant type: "app" for standard OAuth apps, "follower" for follower access.</summary>
    public string GrantType { get; set; } = OAuthGrantTypes.App;

    public List<string> Scopes { get; set; } = new();

    /// <summary>User-friendly label for the grant, if any.</summary>
    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the grant was last used to exchange or refresh a token, if ever.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>IP address of the most recent token exchange, if any.</summary>
    public string? LastUsedIp { get; set; }

    /// <summary>User-agent of the most recent token exchange, if any.</summary>
    public string? LastUsedUserAgent { get; set; }

    public bool IsRevoked { get; set; }

    /// <summary>When the grant stops authorizing; null when it does not.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Whether tokens minted from the grant may read only the last 24 hours.</summary>
    public bool LimitTo24Hours { get; set; }
}
