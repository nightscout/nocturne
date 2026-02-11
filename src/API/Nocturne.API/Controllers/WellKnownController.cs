using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers;

/// <summary>
/// OIDC well-known endpoints for the built-in local identity provider
/// Makes Nocturne act as its own OAuth2/OIDC issuer
/// </summary>
[ApiController]
[Route(".well-known")]
[Tags("OIDC Discovery")]
[ClientPropertyName("oidcDiscovery")]
[AllowAnonymous]
public class WellKnownController : ControllerBase
{
    private readonly LocalIdentityOptions _localOptions;
    private readonly JwtOptions _jwtOptions;
    private readonly OidcOptions _oidcOptions;

    /// <summary>
    /// Creates a new instance of WellKnownController
    /// </summary>
    public WellKnownController(
        IOptions<LocalIdentityOptions> localOptions,
        IOptions<JwtOptions> jwtOptions,
        IOptions<OidcOptions> oidcOptions
    )
    {
        _localOptions = localOptions.Value;
        _jwtOptions = jwtOptions.Value;
        _oidcOptions = oidcOptions.Value;
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
                AuthorizationEndpoint = $"{baseUrl}/auth/local/login",
                TokenEndpoint = $"{baseUrl}/auth/local/token",
                UserinfoEndpoint = $"{baseUrl}/auth/userinfo",
                JwksUri = $"{baseUrl}/.well-known/jwks.json",
                RegistrationEndpoint = _localOptions.Registration.AllowRegistration
                    ? $"{baseUrl}/auth/local/register"
                    : null,
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
                IntrospectionEndpoint = $"{baseUrl}/api/oauth/introspect",
                JwksUri = $"{baseUrl}/.well-known/jwks.json",
                ResponseTypesSupported = new[] { "code" },
                GrantTypesSupported = new[]
                {
                    "authorization_code",
                    "refresh_token",
                    "urn:ietf:params:oauth:grant-type:device_code",
                },
                TokenEndpointAuthMethodsSupported = new[] { "none" },
                ScopesSupported = OAuthScopes.ValidRequestScopes.OrderBy(s => s).ToArray(),
                CodeChallengeMethodsSupported = new[] { "S256" },
                ServiceDocumentation = "https://github.com/nightscout/nocturne",
            }
        );
    }

    private string GetBaseUrl()
    {
        if (!string.IsNullOrEmpty(_oidcOptions.BaseUrl))
        {
            return _oidcOptions.BaseUrl.TrimEnd('/');
        }

        var scheme = Request.Scheme;
        var host = Request.Host.Value;
        return $"{scheme}://{host}";
    }
}

#region Response Models

/// <summary>
/// OpenID Connect Discovery Document
/// See: https://openid.net/specs/openid-connect-discovery-1_0.html
/// </summary>
public class OpenIdConfiguration
{
    public string Issuer { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string? UserinfoEndpoint { get; set; }
    public string JwksUri { get; set; } = string.Empty;
    public string? RegistrationEndpoint { get; set; }
    public string? EndSessionEndpoint { get; set; }
    public string[] ScopesSupported { get; set; } = Array.Empty<string>();
    public string[] ResponseTypesSupported { get; set; } = Array.Empty<string>();
    public string[] ResponseModesSupported { get; set; } = Array.Empty<string>();
    public string[] GrantTypesSupported { get; set; } = Array.Empty<string>();
    public string[] SubjectTypesSupported { get; set; } = Array.Empty<string>();
    public string[] IdTokenSigningAlgValuesSupported { get; set; } = Array.Empty<string>();
    public string[] TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();
    public string[] ClaimsSupported { get; set; } = Array.Empty<string>();
    public string[] CodeChallengeMethodsSupported { get; set; } = Array.Empty<string>();
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
    public string Issuer { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string? DeviceAuthorizationEndpoint { get; set; }
    public string? RevocationEndpoint { get; set; }
    public string? IntrospectionEndpoint { get; set; }
    public string JwksUri { get; set; } = string.Empty;
    public string[] ResponseTypesSupported { get; set; } = Array.Empty<string>();
    public string[] GrantTypesSupported { get; set; } = Array.Empty<string>();
    public string[] TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();
    public string[] ScopesSupported { get; set; } = Array.Empty<string>();
    public string[] CodeChallengeMethodsSupported { get; set; } = Array.Empty<string>();
    public string? ServiceDocumentation { get; set; }
}

#endregion
