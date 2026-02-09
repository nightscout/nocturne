using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// JWT service implementation for access and refresh token management
/// </summary>
public class JwtService : IJwtService
{
    private readonly JwtOptions _options;
    private readonly ILogger<JwtService> _logger;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _validationParameters;

    /// <summary>
    /// Creates a new instance of JwtService
    /// </summary>
    public JwtService(IOptions<JwtOptions> options, ILogger<JwtService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrEmpty(_options.SecretKey) || _options.SecretKey.Length < 32)
        {
            throw new InvalidOperationException("JWT secret key must be at least 32 characters.");
        }

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
        _tokenHandler = new JwtSecurityTokenHandler();

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
        };
    }

    /// <inheritdoc />
    public string GenerateAccessToken(
        SubjectInfo subject,
        IEnumerable<string> permissions,
        IEnumerable<string> roles,
        TimeSpan? lifetime = null
    )
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(
            lifetime ?? TimeSpan.FromMinutes(_options.AccessTokenLifetimeMinutes)
        );
        var jti = Guid.CreateVersion7().ToString();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(
                JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64
            ),
        };

        // Add name if present
        if (!string.IsNullOrEmpty(subject.Name))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Name, subject.Name));
        }

        // Add email if present
        if (!string.IsNullOrEmpty(subject.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, subject.Email));
        }

        // Add OIDC claims if present
        if (!string.IsNullOrEmpty(subject.OidcSubjectId))
        {
            claims.Add(new Claim("oidc_sub", subject.OidcSubjectId));
        }

        if (!string.IsNullOrEmpty(subject.OidcIssuer))
        {
            claims.Add(new Claim("oidc_iss", subject.OidcIssuer));
        }

        // Add roles
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        // Add permissions as custom claim
        foreach (var permission in permissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expires.UtcDateTime,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            SigningCredentials = new SigningCredentials(
                _signingKey,
                SecurityAlgorithms.HmacSha256Signature
            ),
        };

        var token = _tokenHandler.CreateToken(tokenDescriptor);
        return _tokenHandler.WriteToken(token);
    }

    /// <inheritdoc />
    public string GenerateAccessToken(
        SubjectInfo subject,
        IEnumerable<string> permissions,
        IEnumerable<string> roles,
        IEnumerable<string> scopes,
        string? clientId = null,
        TimeSpan? lifetime = null
    )
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(
            lifetime ?? TimeSpan.FromMinutes(_options.AccessTokenLifetimeMinutes)
        );
        var jti = Guid.CreateVersion7().ToString();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(
                JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64
            ),
        };

        // Add name if present
        if (!string.IsNullOrEmpty(subject.Name))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Name, subject.Name));
        }

        // Add email if present
        if (!string.IsNullOrEmpty(subject.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, subject.Email));
        }

        // Add OIDC claims if present
        if (!string.IsNullOrEmpty(subject.OidcSubjectId))
        {
            claims.Add(new Claim("oidc_sub", subject.OidcSubjectId));
        }

        if (!string.IsNullOrEmpty(subject.OidcIssuer))
        {
            claims.Add(new Claim("oidc_iss", subject.OidcIssuer));
        }

        // Add OAuth client ID if present
        if (!string.IsNullOrEmpty(clientId))
        {
            claims.Add(new Claim("client_id", clientId));
        }

        // Add OAuth scopes as space-delimited string (RFC 6749 Section 3.3)
        var scopeList = scopes.ToList();
        if (scopeList.Count > 0)
        {
            claims.Add(new Claim("scope", string.Join(" ", scopeList)));
        }

        // Add roles
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        // Add permissions as custom claim
        foreach (var permission in permissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expires.UtcDateTime,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            SigningCredentials = new SigningCredentials(
                _signingKey,
                SecurityAlgorithms.HmacSha256Signature
            ),
        };

        var token = _tokenHandler.CreateToken(tokenDescriptor);
        return _tokenHandler.WriteToken(token);
    }

    /// <inheritdoc />
    public JwtValidationResult ValidateAccessToken(string token)
    {
        try
        {
            var principal = _tokenHandler.ValidateToken(
                token,
                _validationParameters,
                out var validatedToken
            );

            if (
                validatedToken is not JwtSecurityToken jwtToken
                || !jwtToken.Header.Alg.Equals(
                    SecurityAlgorithms.HmacSha256,
                    StringComparison.InvariantCultureIgnoreCase
                )
            )
            {
                return JwtValidationResult.Failure(
                    "Invalid token algorithm",
                    JwtValidationError.InvalidFormat
                );
            }

            var subjectIdClaim =
                principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (
                string.IsNullOrEmpty(subjectIdClaim)
                || !Guid.TryParse(subjectIdClaim, out var subjectId)
            )
            {
                return JwtValidationResult.Failure(
                    "Invalid subject claim",
                    JwtValidationError.MissingClaims
                );
            }

            // Parse scope claim (space-delimited per RFC 6749)
            var scopeClaim = principal.FindFirst("scope")?.Value;
            var scopeList = string.IsNullOrEmpty(scopeClaim)
                ? new List<string>()
                : scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            var claims = new JwtClaims
            {
                SubjectId = subjectId,
                Name = principal.FindFirst(JwtRegisteredClaimNames.Name)?.Value,
                Email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value,
                Roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
                Permissions = principal.FindAll("permission").Select(c => c.Value).ToList(),
                Scopes = scopeList,
                ClientId = principal.FindFirst("client_id")?.Value,
                JwtId = jwtToken.Id,
                IssuedAt = new DateTimeOffset(jwtToken.IssuedAt, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(jwtToken.ValidTo, TimeSpan.Zero),
            };

            return JwtValidationResult.Success(claims);
        }
        catch (SecurityTokenExpiredException)
        {
            return JwtValidationResult.Failure("Token has expired", JwtValidationError.Expired);
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return JwtValidationResult.Failure(
                "Invalid token signature",
                JwtValidationError.InvalidSignature
            );
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return JwtValidationResult.Failure(
                "Invalid token issuer",
                JwtValidationError.InvalidIssuer
            );
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return JwtValidationResult.Failure(
                "Invalid token audience",
                JwtValidationError.InvalidAudience
            );
        }
        catch (SecurityTokenNotYetValidException)
        {
            return JwtValidationResult.Failure(
                "Token is not yet valid",
                JwtValidationError.NotYetValid
            );
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return JwtValidationResult.Failure(
                $"Token validation failed: {ex.Message}",
                JwtValidationError.InvalidFormat
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during token validation");
            return JwtValidationResult.Failure(
                "An error occurred during token validation",
                JwtValidationError.Unknown
            );
        }
    }

    /// <inheritdoc />
    public string GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(_options.RefreshTokenLengthBytes);
        return Convert.ToBase64String(randomBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <inheritdoc />
    public string HashRefreshToken(string refreshToken)
    {
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <inheritdoc />
    public TimeSpan GetAccessTokenLifetime()
    {
        return TimeSpan.FromMinutes(_options.AccessTokenLifetimeMinutes);
    }
}
