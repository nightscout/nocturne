using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Manages guest access links: creation, activation, validation, and revocation.
/// </summary>
public interface IGuestLinkService
{
    Task<GuestLinkCreationResult> CreateGuestLinkAsync(
        Guid dataOwnerSubjectId,
        Guid createdBySubjectId,
        string label,
        string baseUrl,
        IEnumerable<string>? scopes = null,
        CancellationToken ct = default);

    Task<GuestLinkActivationResult> ActivateAsync(
        string code,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);

    Task<GuestSessionInfo?> ValidateSessionAsync(Guid grantId, CancellationToken ct = default);

    Task<IReadOnlyList<GuestLinkInfo>> GetGuestLinksAsync(
        Guid dataOwnerSubjectId,
        CancellationToken ct = default);

    Task<bool> RevokeAsync(Guid grantId, Guid requestingSubjectId, CancellationToken ct = default);

    Task<int> GetActiveCountAsync(Guid dataOwnerSubjectId, CancellationToken ct = default);
}

public record GuestLinkCreationResult(string Code, string FullUrl, GuestLinkInfo Info);

public record GuestLinkActivationResult(bool Success, GuestSessionInfo? Session, string? Error);

public record GuestSessionInfo(
    Guid GrantId,
    Guid DataOwnerSubjectId,
    IReadOnlyList<string> Scopes,
    string? Label,
    DateTime ExpiresAt);

public record GuestLinkInfo
{
    public required Guid Id { get; init; }
    public required Guid DataOwnerSubjectId { get; init; }
    public required Guid CreatedBySubjectId { get; init; }
    public required string Label { get; init; }
    public required List<string> Scopes { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? ActivatedAt { get; init; }
    public string? ActivatedIp { get; init; }
    public DateTime? RevokedAt { get; init; }
    public required GuestLinkStatus Status { get; init; }
}

public enum GuestLinkStatus
{
    Pending,
    Active,
    Expired,
    Revoked
}
