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
    /// <param name="clientEntityId">The OAuth client entity ID</param>
    /// <param name="subjectId">The subject (user) ID who is granting access</param>
    /// <param name="scopes">The scopes being granted</param>
    /// <param name="grantType">The type of grant (app or follower)</param>
    /// <param name="label">Optional user-friendly label for the grant</param>
    /// <param name="ct">Cancellation token</param>
    Task<OAuthGrantInfo> CreateOrUpdateGrantAsync(
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        string grantType = OAuthScopes.GrantTypeApp,
        string? label = null,
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
    /// Revoke a grant (soft delete). Invalidates all associated refresh tokens, and — because
    /// access tokens carry their grant id — every outstanding access token minted from the grant.
    /// </summary>
    Task RevokeGrantAsync(Guid grantId, CancellationToken ct = default);

    /// <summary>
    /// Whether the grant is revoked, for authenticating an access token that carries a grant id.
    /// A grant that cannot be found is reported as revoked.
    /// </summary>
    /// <param name="grantId">The grant id carried by the access token.</param>
    /// <param name="tenantId">The tenant the presented token is pinned to, already verified against
    /// the request's tenant by the caller. Used to scope the lookup.</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> IsGrantRevokedAsync(Guid grantId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Update last-used tracking on a grant.
    /// </summary>
    Task UpdateLastUsedAsync(
        Guid grantId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default
    );

    /// <summary>
    /// Update grant label and/or scopes. Returns null if grant not found or not owned.
    /// </summary>
    /// <param name="grantId">The grant ID to update</param>
    /// <param name="ownerSubjectId">The owner subject ID (for authorization check)</param>
    /// <param name="label">Optional new label</param>
    /// <param name="scopes">Optional new scopes</param>
    /// <param name="ct">Cancellation token</param>
    Task<OAuthGrantInfo?> UpdateGrantAsync(
        Guid grantId,
        Guid ownerSubjectId,
        string? label = null,
        IEnumerable<string>? scopes = null,
        CancellationToken ct = default
    );
}

/// <summary>
/// Grant information returned by the grant service.
/// </summary>
public class OAuthGrantInfo
{
    /// <summary>Internal grant entity ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant this grant belongs to.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Internal entity ID of the OAuth client, or null for follower grants without a registered client.</summary>
    public Guid? ClientEntityId { get; set; }

    /// <summary>The OAuth client_id string.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Display name of the client shown on the consent screen, if any.</summary>
    public string? ClientDisplayName { get; set; }

    /// <summary>Homepage URI of the client, if any.</summary>
    public string? ClientUri { get; set; }

    /// <summary>Logo URI of the client, if any.</summary>
    public string? LogoUri { get; set; }

    /// <summary>Whether the client comes from the bundled known-app directory.</summary>
    public bool IsKnownClient { get; set; }

    /// <summary>The subject (user) who granted access.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Grant type: "app" for standard OAuth apps, "follower" for follower access.</summary>
    public string GrantType { get; set; } = OAuthScopes.GrantTypeApp;

    /// <summary>OAuth scopes granted.</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>User-friendly label for the grant, if any.</summary>
    public string? Label { get; set; }

    /// <summary>When the grant was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the grant was last used to exchange or refresh a token, if ever.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>IP address of the most recent token exchange, if any.</summary>
    public string? LastUsedIp { get; set; }

    /// <summary>User-agent of the most recent token exchange, if any.</summary>
    public string? LastUsedUserAgent { get; set; }

    /// <summary>Whether the grant has been revoked.</summary>
    public bool IsRevoked { get; set; }
}
