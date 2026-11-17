using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Models.OAuth;

/// <summary>
/// DTO representing an OAuth grant for the management UI
/// </summary>
public class OAuthGrantDto
{
    /// <summary>
    /// Unique identifier of this grant.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// OAuth grant type (e.g. "authorization_code", "direct").
    /// </summary>
    public string GrantType { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client identifier associated with this grant, if any.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Human-readable display name of the client application.
    /// </summary>
    public string? ClientDisplayName { get; set; }

    /// <inheritdoc cref="Nocturne.Core.Contracts.Auth.OAuthGrantInfo.ClientIdentity"/>
    public OAuthClientIdentity ClientIdentity { get; set; }

    /// <summary>
    /// OAuth scopes authorized by this grant.
    /// </summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// User-supplied label for identifying the grant (e.g. "My phone token").
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// When the grant was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the grant's access token was last used.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// User-Agent header from the most recent request using this grant.
    /// </summary>
    public string? LastUsedUserAgent { get; set; }
}

/// <summary>
/// Response containing a list of OAuth grants
/// </summary>
public class OAuthGrantListResponse
{
    /// <summary>
    /// List of all active OAuth grants for the current tenant member.
    /// </summary>
    public List<OAuthGrantDto> Grants { get; set; } = new();
}

/// <summary>
/// Request to update an existing grant's label and/or scopes
/// </summary>
public class UpdateGrantRequest
{
    /// <summary>
    /// New user-facing label for the grant; null leaves the existing label unchanged.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Updated set of authorized scopes; null leaves the existing scopes unchanged.
    /// </summary>
    public List<string>? Scopes { get; set; }
}
