using System.Text.Json.Serialization;

namespace Nocturne.API.Models.OAuth;

/// <summary>
/// Token introspection response (RFC 7662)
/// </summary>
public class TokenIntrospectionResponse
{
    /// <summary>
    /// Whether the token is currently active (not expired or revoked).
    /// </summary>
    public bool Active { get; set; }

    /// <summary>
    /// Space-delimited scopes associated with the token.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// OAuth client identifier that the token was issued to.
    /// </summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>
    /// Subject identifier (tenant member ID) the token represents.
    /// </summary>
    public string? Sub { get; set; }

    /// <summary>
    /// Token expiration time as a Unix timestamp (seconds since epoch).
    /// </summary>
    public long? Exp { get; set; }

    /// <summary>
    /// Token issued-at time as a Unix timestamp (seconds since epoch).
    /// </summary>
    public long? Iat { get; set; }

    /// <summary>
    /// Unique token identifier (JWT ID).
    /// </summary>
    public string? Jti { get; set; }

    /// <summary>
    /// Type of the token (e.g. "Bearer").
    /// </summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}
