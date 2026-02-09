using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for managing OAuth authorization grants.
/// </summary>
public interface IOAuthGrantService
{
    /// <summary>
    /// Create a new grant (user approved app for scopes).
    /// If an active grant already exists for this client+subject, it is updated with the new scopes.
    /// </summary>
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
    /// Revoke a grant (soft delete). Invalidates all associated refresh tokens.
    /// </summary>
    Task RevokeGrantAsync(Guid grantId, CancellationToken ct = default);

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
    /// Create a follower grant (data owner sharing with another user).
    /// </summary>
    Task<OAuthGrantInfo> CreateFollowerGrantAsync(
        Guid dataOwnerSubjectId,
        Guid followerSubjectId,
        IEnumerable<string> scopes,
        string? label = null,
        Guid? createdFromInviteId = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Get active follower grants where the specified subject is the follower.
    /// </summary>
    Task<IReadOnlyList<OAuthGrantInfo>> GetGrantsAsFollowerAsync(
        Guid followerSubjectId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Get an active follower grant for a specific data-owner/follower pair.
    /// </summary>
    Task<OAuthGrantInfo?> GetActiveFollowerGrantAsync(
        Guid dataOwnerSubjectId,
        Guid followerSubjectId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Update grant label and/or scopes. Returns null if grant not found or not owned.
    /// </summary>
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
    public Guid Id { get; set; }
    public Guid ClientEntityId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? ClientDisplayName { get; set; }
    public bool IsKnownClient { get; set; }
    public Guid SubjectId { get; set; }
    public string GrantType { get; set; } = OAuthScopes.GrantTypeApp;
    public List<string> Scopes { get; set; } = new();
    public string? Label { get; set; }
    public Guid? FollowerSubjectId { get; set; }
    public string? FollowerName { get; set; }
    public string? FollowerEmail { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    public string? LastUsedUserAgent { get; set; }
    public bool IsRevoked { get; set; }
}
