namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Endpoint and claim configuration for a plain OAuth2 provider (one with no OpenID Connect
/// discovery document or ID token). These values stand in for what an OIDC provider would otherwise
/// advertise via its discovery document, plus a mapping describing how to read identity from the
/// provider's userinfo response. Everything here is data — no provider is special-cased in code.
/// </summary>
/// <seealso cref="OidcProviderType.OAuth2"/>
/// <seealso cref="OidcProvider"/>
public class OAuth2ProviderSettings
{
    /// <summary>The provider's OAuth2 authorization endpoint (where the user is sent to grant access).</summary>
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    /// <summary>The provider's OAuth2 token endpoint (where the authorization code is exchanged for an access token).</summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>The endpoint returning the authenticated user's profile, called with the access token.</summary>
    public string UserInfoEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Optional endpoint returning the user's email addresses when the profile response omits one.
    /// Expected to return a JSON array of objects with <c>email</c>, <c>primary</c>, and <c>verified</c>
    /// fields; the primary verified address is used. Leave null for providers that return the email
    /// in the userinfo response.
    /// </summary>
    public string? UserInfoEmailEndpoint { get; set; }

    /// <summary>
    /// Maps standard identity claims to the property names in the userinfo response. Recognised keys
    /// are <c>sub</c>, <c>email</c>, <c>name</c>, <c>preferred_username</c>, and <c>picture</c>. A key
    /// left unset falls back to the standard name itself (e.g. <c>email</c> reads the <c>email</c>
    /// property). <c>sub</c> is required and may be a string or a number.
    /// </summary>
    public Dictionary<string, string> ClaimMappings { get; set; } = new();
}
