using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Constants;

namespace Nocturne.API.Controllers;

/// <summary>
/// OIDC well-known endpoints for the built-in local identity provider.
/// Makes Nocturne act as its own OAuth 2.0 / OIDC issuer.
/// </summary>
/// <remarks>
/// Implements the OpenID Connect Discovery 1.0 specification:
/// <list type="bullet">
///   <item><description><c>GET /.well-known/openid-configuration</c> — provider metadata document.</description></item>
///   <item><description><c>GET /.well-known/jwks.json</c> — JSON Web Key Set used to verify JWT signatures.</description></item>
/// </list>
///
/// Both endpoints are <see cref="AllowAnonymousAttribute"/> and are consumed by OAuth clients during
/// the dynamic discovery phase. The issuer URL and signing key are derived from <see cref="JwtOptions"/>.
/// </remarks>
/// <seealso cref="JwtOptions"/>
[ApiController]
[Route(".well-known")]
[Tags("OIDC Discovery")]
[ClientPropertyName("oidcDiscovery")]
[AllowAnonymous]
public class WellKnownController : ControllerBase
{
    private readonly JwtOptions _jwtOptions;
    private readonly BaseDomainOptions _baseDomain;

    /// <summary>
    /// Creates a new instance of WellKnownController
    /// </summary>
    public WellKnownController(
        IOptions<JwtOptions> jwtOptions,
        IOptions<BaseDomainOptions> baseDomainOptions
    )
    {
        _jwtOptions = jwtOptions.Value;
        _baseDomain = baseDomainOptions.Value;
    }

    /// <summary>
    /// OpenID Connect Discovery Document
    /// </summary>
    [HttpGet("openid-configuration")]
    [ProducesResponseType(typeof(OpenIdConfiguration), StatusCodes.Status200OK)]
    public ActionResult<OpenIdConfiguration> GetOpenIdConfiguration()
    {
        var baseUrl = GetBaseUrl();

        return Ok(
            new OpenIdConfiguration
            {
                Issuer = _jwtOptions.Issuer,
                AuthorizationEndpoint = $"{baseUrl}/api/oauth/authorize",
                TokenEndpoint = $"{baseUrl}/api/oauth/token",
                UserinfoEndpoint = $"{baseUrl}/auth/userinfo",
                JwksUri = $"{baseUrl}/.well-known/jwks.json",
                RegistrationEndpoint = null,
                ScopesSupported = new[] { "openid", "profile", "email", "offline_access" },
                ResponseTypesSupported = new[]
                {
                    "code",
                    "token",
                    "id_token",
                    "code token",
                    "code id_token",
                    "token id_token",
                    "code token id_token",
                },
                ResponseModesSupported = new[] { "query", "fragment", "form_post" },
                GrantTypesSupported = new[] { "authorization_code", "refresh_token", "password" },
                SubjectTypesSupported = new[] { "public" },
                IdTokenSigningAlgValuesSupported = new[] { "HS256" },
                TokenEndpointAuthMethodsSupported = new[]
                {
                    "client_secret_basic",
                    "client_secret_post",
                    "none",
                },
                ClaimsSupported = new[]
                {
                    "sub",
                    "name",
                    "email",
                    "email_verified",
                    "iat",
                    "exp",
                    "iss",
                    "aud",
                },
                CodeChallengeMethodsSupported = new[] { "plain", "S256" },
                ServiceDocumentation = "https://github.com/nightscout/nocturne",
            }
        );
    }

    /// <summary>
    /// JSON Web Key Set (JWKS) - for token signature verification
    /// Note: Since we use HMAC symmetric keys, we only expose the algorithm info
    /// Actual key verification happens server-side
    /// </summary>
    [HttpGet("jwks.json")]
    [ProducesResponseType(typeof(JsonWebKeySet), StatusCodes.Status200OK)]
    public ActionResult<JsonWebKeySet> GetJwks()
    {
        // For HMAC, we don't expose the actual key - just indicate the algorithm
        // This is primarily for documentation purposes
        // Actual token validation uses the server-side secret
        return Ok(
            new JsonWebKeySet
            {
                Keys = new[]
                {
                    new JsonWebKey
                    {
                        Kty = "oct",
                        Use = "sig",
                        Alg = "HS256",
                        Kid = "nocturne-local-key-1",
                    },
                },
            }
        );
    }

    /// <summary>
    /// OAuth 2.0 Authorization Server Metadata (RFC 8414).
    /// Includes Nocturne's OAuth scope taxonomy and supported grant types.
    /// </summary>
    [HttpGet("oauth-authorization-server")]
    [ProducesResponseType(typeof(OAuthAuthorizationServerMetadata), StatusCodes.Status200OK)]
    public ActionResult<OAuthAuthorizationServerMetadata> GetOAuthMetadata()
    {
        var baseUrl = GetBaseUrl();

        return Ok(
            new OAuthAuthorizationServerMetadata
            {
                Issuer = _jwtOptions.Issuer,
                AuthorizationEndpoint = $"{baseUrl}/api/oauth/authorize",
                TokenEndpoint = $"{baseUrl}/api/oauth/token",
                DeviceAuthorizationEndpoint = $"{baseUrl}/api/oauth/device",
                RevocationEndpoint = $"{baseUrl}/api/oauth/revoke",
                // Introspection is first-party self-introspection: the caller authenticates with
                // its own session or bearer credential, not a client secret, and there is no
                // registered token_endpoint_auth_method that describes that. It is left out of the
                // advertised metadata so an external resource server reading discovery does not
                // treat it as a client-authenticated RFC 7662 endpoint and get an undocumented 401.
                RegistrationEndpoint = $"{baseUrl}/api/oauth/register",
                JwksUri = $"{baseUrl}/.well-known/jwks.json",
                ResponseTypesSupported = new[] { "code" },
                GrantTypesSupported = new[]
                {
                    "authorization_code",
                    "refresh_token",
                    "urn:ietf:params:oauth:grant-type:device_code",
                },
                TokenEndpointAuthMethodsSupported = new[] { "none" },
                ScopesSupported = Enum.GetValues<OAuthScope>(),
                CodeChallengeMethodsSupported = new[] { "S256" },
                ServiceDocumentation = "https://github.com/nightscout/nocturne",
            }
        );
    }

    private string GetBaseUrl()
    {
        return _baseDomain.PublicOrigin ?? $"{Request.Scheme}://{Request.Host}";
    }
}

#region Response Models

/// <summary>
/// OpenID Connect Discovery Document
/// See: https://openid.net/specs/openid-connect-discovery-1_0.html
/// </summary>
/// <remarks>
/// Discovery metadata member names are fixed by the specifications, so every member is pinned
/// with <see cref="JsonPropertyNameAttribute"/> rather than left to the camelCase policy MVC
/// applies by default. Pinning them on the model also keeps the generated OpenAPI schema — and
/// therefore the generated clients — describing the names that actually go on the wire.
/// </remarks>
public class OpenIdConfiguration
{
    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("userinfo_endpoint")]
    public string? UserinfoEndpoint { get; set; }

    [JsonPropertyName("jwks_uri")]
    public string JwksUri { get; set; } = string.Empty;

    [JsonPropertyName("registration_endpoint")]
    public string? RegistrationEndpoint { get; set; }

    [JsonPropertyName("end_session_endpoint")]
    public string? EndSessionEndpoint { get; set; }

    [JsonPropertyName("scopes_supported")]
    public string[] ScopesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("response_types_supported")]
    public string[] ResponseTypesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("response_modes_supported")]
    public string[] ResponseModesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("grant_types_supported")]
    public string[] GrantTypesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("subject_types_supported")]
    public string[] SubjectTypesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("id_token_signing_alg_values_supported")]
    public string[] IdTokenSigningAlgValuesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public string[] TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("claims_supported")]
    public string[] ClaimsSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("code_challenge_methods_supported")]
    public string[] CodeChallengeMethodsSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("service_documentation")]
    public string? ServiceDocumentation { get; set; }
}

/// <summary>
/// JSON Web Key Set
/// </summary>
public class JsonWebKeySet
{
    public JsonWebKey[] Keys { get; set; } = Array.Empty<JsonWebKey>();
}

/// <summary>
/// JSON Web Key
/// </summary>
public class JsonWebKey
{
    public string Kty { get; set; } = string.Empty;
    public string? Use { get; set; }
    public string? Alg { get; set; }
    public string? Kid { get; set; }
    public string? N { get; set; } // RSA modulus
    public string? E { get; set; } // RSA exponent
}

/// <summary>
/// OAuth 2.0 Authorization Server Metadata
/// See: https://datatracker.ietf.org/doc/html/rfc8414
/// </summary>
public class OAuthAuthorizationServerMetadata
{
    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("device_authorization_endpoint")]
    public string? DeviceAuthorizationEndpoint { get; set; }

    [JsonPropertyName("revocation_endpoint")]
    public string? RevocationEndpoint { get; set; }

    [JsonPropertyName("introspection_endpoint")]
    public string? IntrospectionEndpoint { get; set; }

    [JsonPropertyName("registration_endpoint")]
    public string? RegistrationEndpoint { get; set; }

    [JsonPropertyName("jwks_uri")]
    public string JwksUri { get; set; } = string.Empty;

    [JsonPropertyName("response_types_supported")]
    public string[] ResponseTypesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("grant_types_supported")]
    public string[] GrantTypesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public string[] TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("scopes_supported")]
    public OAuthScope[] ScopesSupported { get; set; } = Array.Empty<OAuthScope>();

    [JsonPropertyName("code_challenge_methods_supported")]
    public string[] CodeChallengeMethodsSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("service_documentation")]
    public string? ServiceDocumentation { get; set; }
}

#endregion
