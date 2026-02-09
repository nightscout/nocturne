namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for managing follower invite links.
/// Allows data owners to share their data with users who don't have accounts yet.
/// </summary>
public interface IFollowerInviteService
{
    /// <summary>
    /// Create a new invite link.
    /// </summary>
    /// <param name="ownerSubjectId">The data owner creating the invite</param>
    /// <param name="scopes">Scopes to grant when accepted</param>
    /// <param name="label">Optional label for the grant</param>
    /// <param name="expiresIn">How long until the invite expires (default: 7 days)</param>
    /// <param name="maxUses">Maximum uses (null = unlimited, 1 = single-use)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The created invite with the raw token (only returned once)</returns>
    Task<FollowerInviteResult> CreateInviteAsync(
        Guid ownerSubjectId,
        IEnumerable<string> scopes,
        string? label = null,
        TimeSpan? expiresIn = null,
        int? maxUses = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get invite information by token (for the accept page).
    /// </summary>
    /// <param name="token">The raw invite token</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Invite info if valid, null if not found or invalid</returns>
    Task<FollowerInviteInfo?> GetInviteByTokenAsync(
        string token,
        CancellationToken ct = default);

    /// <summary>
    /// Accept an invite and create the follower grant.
    /// </summary>
    /// <param name="token">The raw invite token</param>
    /// <param name="followerSubjectId">The user accepting the invite</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result indicating success or failure reason</returns>
    Task<AcceptInviteResult> AcceptInviteAsync(
        string token,
        Guid followerSubjectId,
        CancellationToken ct = default);

    /// <summary>
    /// Get all invites created by an owner.
    /// </summary>
    /// <param name="ownerSubjectId">The data owner</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of invites (including expired/revoked for history)</returns>
    Task<IReadOnlyList<FollowerInviteInfo>> GetInvitesForOwnerAsync(
        Guid ownerSubjectId,
        CancellationToken ct = default);

    /// <summary>
    /// Revoke an invite so it can no longer be used.
    /// </summary>
    /// <param name="inviteId">The invite ID</param>
    /// <param name="ownerSubjectId">The owner (for authorization)</param>
    /// <param name="ct">Cancellation token</param>
    Task RevokeInviteAsync(
        Guid inviteId,
        Guid ownerSubjectId,
        CancellationToken ct = default);
}

/// <summary>
/// Result of creating an invite. Contains the raw token which is only shown once.
/// </summary>
public class FollowerInviteResult
{
    /// <summary>
    /// The invite ID
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The raw invite token. Only returned at creation time.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Full invite URL to share
    /// </summary>
    public string InviteUrl { get; set; } = string.Empty;

    /// <summary>
    /// When the invite expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Information about an invite for display/acceptance.
/// </summary>
public class FollowerInviteInfo
{
    /// <summary>
    /// The invite ID
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The data owner's subject ID
    /// </summary>
    public Guid OwnerSubjectId { get; set; }

    /// <summary>
    /// The data owner's display name
    /// </summary>
    public string? OwnerName { get; set; }

    /// <summary>
    /// The data owner's email
    /// </summary>
    public string? OwnerEmail { get; set; }

    /// <summary>
    /// Scopes that will be granted
    /// </summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Optional label for the grant
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// When the invite expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Maximum uses (null = unlimited)
    /// </summary>
    public int? MaxUses { get; set; }

    /// <summary>
    /// How many times the invite has been used
    /// </summary>
    public int UseCount { get; set; }

    /// <summary>
    /// When the invite was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Whether the invite is currently valid
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Whether the invite has expired
    /// </summary>
    public bool IsExpired { get; set; }

    /// <summary>
    /// Whether the invite has been revoked
    /// </summary>
    public bool IsRevoked { get; set; }

    /// <summary>
    /// List of users who have used this invite
    /// </summary>
    public List<InviteUsage> UsedBy { get; set; } = new();
}

/// <summary>
/// Information about who used an invite.
/// </summary>
public class InviteUsage
{
    /// <summary>
    /// The follower's subject ID
    /// </summary>
    public Guid FollowerSubjectId { get; set; }

    /// <summary>
    /// The follower's display name
    /// </summary>
    public string? FollowerName { get; set; }

    /// <summary>
    /// The follower's email
    /// </summary>
    public string? FollowerEmail { get; set; }

    /// <summary>
    /// When the invite was used
    /// </summary>
    public DateTime UsedAt { get; set; }
}

/// <summary>
/// Result of accepting an invite.
/// </summary>
public class AcceptInviteResult
{
    /// <summary>
    /// Whether the invite was accepted successfully
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error code if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Human-readable error description if failed
    /// </summary>
    public string? ErrorDescription { get; set; }

    /// <summary>
    /// The created grant ID if successful
    /// </summary>
    public Guid? GrantId { get; set; }

    /// <summary>
    /// Create a successful result
    /// </summary>
    public static AcceptInviteResult Succeeded(Guid grantId) =>
        new() { Success = true, GrantId = grantId };

    /// <summary>
    /// Create a failed result
    /// </summary>
    public static AcceptInviteResult Failed(string error, string description) =>
        new() { Success = false, Error = error, ErrorDescription = description };
}
