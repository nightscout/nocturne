using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for OIDC JWT tokens from configured providers.
/// Validates tokens against the provider's JWKS (JSON Web Key Set).
/// </summary>
public class OidcTokenHandler : IAuthHandler
{
    /// <summary>
    /// Handler priority (100 - first in chain, highest priority)
    /// </summary>
    public int Priority => 100;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "OidcTokenHandler";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OidcTokenHandler> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    // Cache for OIDC configuration managers (keyed by issuer URL)
    private static readonly Dictionary<
        string,
        ConfigurationManager<OpenIdConnectConfiguration>
    > _configManagers = new();
    private static readonly object _configManagerLock = new();

    /// <summary>
    /// Creates a new instance of OidcTokenHandler
    /// </summary>
    public OidcTokenHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<OidcTokenHandler> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        var token = context.Request.GetAuthorizationCredential();

        if (!TokenFormat.IsJwt(token))
        {
            return AuthResult.Skip();
        }

        // Try to decode the token to get the issuer (without validating signature yet)
        string? issuer;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);
            issuer = jwtToken.Issuer;
        }
        catch
        {
            // Not a valid JWT, let another handler try
            return AuthResult.Skip();
        }

        if (string.IsNullOrEmpty(issuer))
        {
            return AuthResult.Skip();
        }

        // Check if this issuer matches a configured OIDC provider
        using var scope = _scopeFactory.CreateScope();
        var oidcProviderService = scope.ServiceProvider.GetRequiredService<IOidcProviderService>();

        OidcProvider? provider;
        try
        {
            provider = await oidcProviderService.GetProviderByIssuerAsync(issuer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error looking up OIDC provider for issuer {Issuer}", issuer);
            return AuthResult.Skip();
        }

        if (provider == null || !provider.IsEnabled)
        {
            // Not from a configured provider, let legacy JWT handler try
            _logger.LogDebug(
                "Token issuer {Issuer} not found in configured OIDC providers",
                issuer
            );
            return AuthResult.Skip();
        }

        // Validate the token against the provider's JWKS
        try
        {
            var validationResult = await ValidateOidcTokenAsync(token, provider);

            if (!validationResult.IsValid)
            {
                _logger.LogDebug("OIDC token validation failed: {Error}", validationResult.Error);
                return AuthResult.Failure(validationResult.Error ?? "Token validation failed");
            }

            // Extract claims and build auth context
            var subjectService = scope.ServiceProvider.GetRequiredService<ISubjectService>();
            var authContext = await BuildAuthContextAsync(
                validationResult.Claims!,
                provider,
                subjectService,
                token
            );

            _logger.LogDebug(
                "OIDC token authentication successful for subject {SubjectName} from provider {Provider}",
                authContext.SubjectName,
                provider.Name
            );

            return AuthResult.Success(authContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error validating OIDC token from provider {Provider}",
                provider.Name
            );
            return AuthResult.Failure("Token validation error");
        }
    }

    /// <summary>
    /// Validate an OIDC token against the provider's JWKS
    /// </summary>
    private async Task<OidcValidationResult> ValidateOidcTokenAsync(
        string token,
        OidcProvider provider
    )
    {
        var configManager = GetOrCreateConfigurationManager(provider.IssuerUrl);

        try
        {
            var config = await configManager.GetConfigurationAsync(CancellationToken.None);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = provider.IssuerUrl,
                ValidateAudience = true,
                ValidAudience = provider.ClientId,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                IssuerSigningKeys = config.SigningKeys,
                ValidateIssuerSigningKey = true,
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = tokenHandler.ValidateToken(
                token,
                validationParameters,
                out var validatedToken
            );

            if (validatedToken is not JwtSecurityToken jwtToken)
            {
                return new OidcValidationResult { IsValid = false, Error = "Invalid token format" };
            }

            return new OidcValidationResult
            {
                IsValid = true,
                Claims = principal,
                Token = jwtToken,
            };
        }
        catch (SecurityTokenExpiredException)
        {
            return new OidcValidationResult { IsValid = false, Error = "Token has expired" };
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return new OidcValidationResult { IsValid = false, Error = "Invalid token audience" };
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return new OidcValidationResult { IsValid = false, Error = "Invalid token issuer" };
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // Key might have rotated, try refreshing the config
            configManager.RequestRefresh();
            return new OidcValidationResult
            {
                IsValid = false,
                Error = "Token signing key not found",
            };
        }
        catch (SecurityTokenValidationException ex)
        {
            return new OidcValidationResult { IsValid = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get or create an OIDC configuration manager for the issuer
    /// </summary>
    private ConfigurationManager<OpenIdConnectConfiguration> GetOrCreateConfigurationManager(
        string issuerUrl
    )
    {
        lock (_configManagerLock)
        {
            if (_configManagers.TryGetValue(issuerUrl, out var existing))
            {
                return existing;
            }

            var metadataAddress = issuerUrl.TrimEnd('/') + "/.well-known/openid-configuration";
            var httpClient = _httpClientFactory.CreateClient("OidcProvider");

            var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(httpClient)
                {
                    RequireHttps = !issuerUrl.StartsWith(
                        "http://localhost",
                        StringComparison.OrdinalIgnoreCase
                    ),
                }
            );

            // Automatically refresh configuration (default is 24 hours)
            configManager.AutomaticRefreshInterval = TimeSpan.FromHours(12);

            _configManagers[issuerUrl] = configManager;
            return configManager;
        }
    }

    /// <summary>
    /// Build an AuthContext from validated OIDC claims
    /// </summary>
    private async Task<AuthContext> BuildAuthContextAsync(
        ClaimsPrincipal claims,
        OidcProvider provider,
        ISubjectService subjectService,
        string token
    )
    {
        // Extract standard claims
        var oidcSubjectId =
            claims.FindFirst("sub")?.Value ?? claims.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = claims.FindFirst("email")?.Value ?? claims.FindFirst(ClaimTypes.Email)?.Value;
        var name =
            claims.FindFirst("name")?.Value
            ?? claims.FindFirst("preferred_username")?.Value
            ?? claims.FindFirst(ClaimTypes.Name)?.Value
            ?? email
            ?? oidcSubjectId;

        // Apply custom claim mappings from provider configuration
        if (provider.ClaimMappings.Count > 0)
        {
            foreach (var mapping in provider.ClaimMappings)
            {
                var claimValue = claims.FindFirst(mapping.Key)?.Value;
                if (!string.IsNullOrEmpty(claimValue))
                {
                    switch (mapping.Value.ToLowerInvariant())
                    {
                        case "email":
                            email = claimValue;
                            break;
                        case "name":
                            name = claimValue;
                            break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(oidcSubjectId))
        {
            throw new InvalidOperationException("OIDC token missing subject identifier");
        }

        // Find or create subject from OIDC identity
        var subject = await subjectService.FindOrCreateFromOidcAsync(
            provider.Id,
            oidcSubjectId,
            provider.IssuerUrl,
            email,
            name,
            provider.DefaultRoles
        );

        // Get permissions for the subject
        var permissions = await subjectService.GetSubjectPermissionsAsync(subject.Id);
        var roles = await subjectService.GetSubjectRolesAsync(subject.Id);

        // Update last login
        _ = subjectService.UpdateLastLoginAsync(subject.Id);

        // Extract expiration from token
        var expClaim = claims.FindFirst("exp")?.Value;
        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrEmpty(expClaim) && long.TryParse(expClaim, out var expUnix))
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(expUnix);
        }

        return new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.OidcToken,
            SubjectId = subject.Id,
            SubjectName = subject.Name,
            Email = email,
            OidcSubjectId = oidcSubjectId,
            OidcIssuer = provider.IssuerUrl,
            Permissions = permissions,
            Roles = roles,
            Scopes = provider.Scopes,
            RawToken = token,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>
    /// Result of OIDC token validation
    /// </summary>
    private class OidcValidationResult
    {
        public bool IsValid { get; set; }
        public string? Error { get; set; }
        public ClaimsPrincipal? Claims { get; set; }
        public JwtSecurityToken? Token { get; set; }
    }
}
