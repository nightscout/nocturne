namespace Nocturne.Core.Models.Authorization;

/// <seealso cref="SubjectOidcIdentity"/>
/// <seealso cref="OidcDiscoveryDocument"/>
public class OidcProvider
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The authentication protocol this provider speaks. Defaults to standards-compliant OIDC;
    /// <see cref="OidcProviderType.OAuth2"/> selects the plain-OAuth2 flow driven by <see cref="OAuth2"/>.
    /// </summary>
    public OidcProviderType ProviderType { get; set; } = OidcProviderType.Oidc;

    /// <summary>
    /// Endpoint and claim configuration for a plain OAuth2 provider. Required when
    /// <see cref="ProviderType"/> is <see cref="OidcProviderType.OAuth2"/>; ignored for OIDC.
    /// </summary>
    public OAuth2ProviderSettings? OAuth2 { get; set; }

    /// <summary>
    /// Issuer URL. For OIDC this is the discovery base; for OAuth2 it is the stable identity
    /// namespace external identities are keyed by (e.g. <c>https://github.com</c>).
    /// </summary>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 client ID
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 client secret (decrypted)
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// OAuth2 scopes to request
    /// </summary>
    public List<string> Scopes { get; set; } = new() { "openid", "profile", "email" };

    /// <summary>
    /// Claim mappings (OIDC claim name -> Nocturne claim name)
    /// </summary>
    public Dictionary<string, string> ClaimMappings { get; set; } = new();

    /// <summary>
    /// Default roles to assign to new users from this provider
    /// </summary>
    public List<string> DefaultRoles { get; set; } = new() { "readable" };

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Display order in login UI
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Icon URL or CSS class
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Button color for login UI
    /// </summary>
    public string? ButtonColor { get; set; }

    /// <summary>
    /// OIDC discovery document (if cached)
    /// </summary>
    public OidcDiscoveryDocument? DiscoveryDocument { get; set; }

    public DateTime? DiscoveryCachedAt { get; set; }
}

/// <summary>
/// OIDC Discovery Document (OpenID Connect Configuration)
/// </summary>
public class OidcDiscoveryDocument
{
    public string Issuer { get; set; } = string.Empty;

    public string AuthorizationEndpoint { get; set; } = string.Empty;

    public string TokenEndpoint { get; set; } = string.Empty;

    public string? UserInfoEndpoint { get; set; }

    /// <summary>
    /// End session (logout) endpoint URL
    /// </summary>
    public string? EndSessionEndpoint { get; set; }

    /// <summary>
    /// JWKS (JSON Web Key Set) URI
    /// </summary>
    public string JwksUri { get; set; } = string.Empty;

    /// <summary>
    /// Token introspection endpoint URL
    /// </summary>
    public string? IntrospectionEndpoint { get; set; }

    public string? RevocationEndpoint { get; set; }

    public List<string> ResponseTypesSupported { get; set; } = new();

    public List<string> GrantTypesSupported { get; set; } = new();

    public List<string> ScopesSupported { get; set; } = new();

    public List<string> IdTokenSigningAlgValuesSupported { get; set; } = new();
}
