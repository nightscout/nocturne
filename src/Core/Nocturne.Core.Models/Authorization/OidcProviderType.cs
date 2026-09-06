using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// The authentication protocol an external identity provider speaks.
/// </summary>
/// <remarks>
/// Standards-compliant OpenID Connect providers are resolved entirely through their discovery
/// document and ID token. Plain OAuth2 providers publish no discovery document and issue no ID
/// token, so their endpoints are supplied as configuration and identity is read from a userinfo
/// endpoint. The protocol is provider-agnostic: any OAuth2 provider (GitHub, GitLab, Discord, …)
/// is a matter of configuration, not code.
/// </remarks>
/// <seealso cref="OidcProvider"/>
/// <seealso cref="OAuth2ProviderSettings"/>
[JsonConverter(typeof(JsonStringEnumConverter<OidcProviderType>))]
public enum OidcProviderType
{
    /// <summary>
    /// Standards-compliant OpenID Connect provider. Endpoints come from the discovery document;
    /// identity comes from the ID token.
    /// </summary>
    Oidc = 0,

    /// <summary>
    /// Plain OAuth2 provider. Endpoints are supplied via <see cref="OAuth2ProviderSettings"/> and
    /// identity is read from the configured userinfo endpoint (no ID token is issued).
    /// </summary>
    OAuth2 = 1,
}
