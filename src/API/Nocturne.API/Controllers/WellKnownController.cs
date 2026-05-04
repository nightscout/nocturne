using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
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
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates a new instance of WellKnownController
    /// </summary>
    public WellKnownController(
        IOptions<JwtOptions> jwtOptions,
        IConfiguration configuration
    )
    {
        _jwtOptions = jwtOptions.Value;
        _configuration = configuration;
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
                IntrospectionEndpoint = $"{baseUrl}/api/oauth/introspect",
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
                ScopesSupported = new[]
                {
                    OAuthScope.EntriesRead,
                    OAuthScope.EntriesReadWrite,
                    OAuthScope.TreatmentsRead,
                    OAuthScope.TreatmentsReadWrite,
                    OAuthScope.DeviceStatusRead,
                    OAuthScope.DeviceStatusReadWrite,
                    OAuthScope.ProfileRead,
                    OAuthScope.ProfileReadWrite,
                    OAuthScope.NotificationsRead,
                    OAuthScope.NotificationsReadWrite,
                    OAuthScope.ReportsRead,
                    OAuthScope.IdentityRead,
                    OAuthScope.SharingReadWrite,
                    OAuthScope.HealthRead,
                    OAuthScope.FullAccess,
                },
                CodeChallengeMethodsSupported = new[] { "S256" },
                ServiceDocumentation = "https://github.com/nightscout/nocturne",
            }
        );
    }

    private string GetBaseUrl()
    {
        var configuredUrl = _configuration[ServiceNames.ConfigKeys.BaseUrl];
        if (!string.IsNullOrEmpty(configuredUrl))
        {
            return configuredUrl.TrimEnd('/');
        }

        return $"{Request.Scheme}://{Request.Host}";
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
    public string? RegistrationEndpoint { get; set; }
    public string JwksUri { get; set; } = string.Empty;
    public string[] ResponseTypesSupported { get; set; } = Array.Empty<string>();
    public string[] GrantTypesSupported { get; set; } = Array.Empty<string>();
    public string[] TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();
    public OAuthScope[] ScopesSupported { get; set; } = Array.Empty<OAuthScope>();
    public string[] CodeChallengeMethodsSupported { get; set; } = Array.Empty<string>();
    public string? ServiceDocumentation { get; set; }
}

#endregion
