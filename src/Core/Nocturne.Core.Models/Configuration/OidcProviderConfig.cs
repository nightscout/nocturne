using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Operator-defined OIDC provider configuration from appsettings.json.
/// When one or more providers are defined in <c>Oidc:Providers[]</c>, the database
/// is bypassed entirely and the management UI is hidden.
/// </summary>
/// <remarks>
/// <see cref="ClientSecret"/> should be supplied via environment variables or a secrets manager,
/// not committed to appsettings.json.
/// </remarks>
/// <seealso cref="OidcOptions"/>
/// <seealso cref="Nocturne.Core.Models.Authorization.OidcProvider"/>
public class OidcProviderConfig
{
    /// <summary>Display name for this provider (shown on the login screen).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Authentication protocol. Defaults to <see cref="OidcProviderType.Oidc"/>; set to
    /// <see cref="OidcProviderType.OAuth2"/> for a plain OAuth2 provider, supplying <see cref="OAuth2"/>.
    /// </summary>
    public OidcProviderType ProviderType { get; set; } = OidcProviderType.Oidc;

    /// <summary>
    /// Endpoint and claim configuration for a plain OAuth2 provider. Required when
    /// <see cref="ProviderType"/> is <see cref="OidcProviderType.OAuth2"/>.
    /// </summary>
    public OAuth2ProviderSettings? OAuth2 { get; set; }

    /// <summary>
    /// Issuer URL. OIDC discovery base; for OAuth2 the stable identity namespace (e.g. <c>https://github.com</c>).
    /// </summary>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>OAuth2 client ID registered with the provider.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client secret. Should be supplied via secrets manager, not checked into source.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OIDC scopes to request during authorization.</summary>
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>Default <see cref="Nocturne.Core.Models.Authorization.Role"/> names to assign to new users from this provider.</summary>
    public List<string> DefaultRoles { get; set; } = ["readable"];

    /// <summary>Whether this provider is enabled for login.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Display order in the login UI (lower numbers appear first).</summary>
    public int DisplayOrder { get; set; } = 0;

    /// <summary>
    /// Known slugs: "google", "apple", "microsoft", "github".
    /// Any other value is treated as a URL.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>Button background color for the login UI (CSS color value).</summary>
    public string? ButtonColor { get; set; }
}
