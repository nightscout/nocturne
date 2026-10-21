namespace Nocturne.API.Services.Auth;

/// <summary>
/// Response containing the newly created direct grant and plaintext token
/// </summary>
public class CreateDirectGrantResponse
{
    public Guid Id { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool LimitTo24Hours { get; set; }
}

/// <summary>
/// Direct grant information (never includes the token)
/// </summary>
public class DirectGrantDto
{
    public Guid Id { get; set; }
    public Guid SubjectId { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the grant stops authenticating. Null means it never does.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// True when this grant was created from a migrated Nightscout API secret
    /// rather than as a scoped <c>noc_</c> token.
    /// </summary>
    public bool IsLegacy { get; set; }

    /// <summary>
    /// True when the grant may read only the last 24 hours of time-series data.
    /// </summary>
    public bool LimitTo24Hours { get; set; }
}
