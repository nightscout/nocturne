using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Authentication controller that provides authentication verification for legacy Nightscout compatibility.
/// </summary>
/// <seealso cref="VerifyAuthResponse"/>
[ApiController]
[Route("api/v1")]
public class AuthenticationController : ControllerBase
{
    private readonly ILogger<AuthenticationController> _logger;

    public AuthenticationController(ILogger<AuthenticationController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verify authentication status and permissions for the current request
    /// This endpoint provides 1:1 compatibility with Nightscout's /api/v1/verifyauth endpoint
    /// </summary>
    /// <returns>Authentication status and permission information</returns>
    [HttpGet("verifyauth")]
    [NightscoutEndpoint("/api/v1/verifyauth")]
    [ProducesResponseType(typeof(VerifyAuthResponse), 200)]
    public ActionResult<VerifyAuthResponse> VerifyAuthentication()
    {
        try
        {
            var authContext = HttpContext.GetAuthContext();

            // Determine the response format based on authentication status
            if (authContext?.IsAuthenticated == true)
            {
                var canRead = HttpContext.CanRead();
                var canWrite = HttpContext.CanWrite();
                var isAdmin = HttpContext.IsAdmin();

                // For JWT/OIDC token authentication, use the detailed response format
                if (authContext.AuthType != AuthType.ApiKey)
                {
                    var response = new VerifyAuthResponse
                    {
                        Message = new AuthResponseMessage
                        {
                            RoleFound = "FOUND",
                            Message = "OK",
                            CanRead = canRead,
                            CanWrite = canWrite,
                            IsAdmin = isAdmin,
                            Permissions = string.Join(",", authContext.Permissions),
                        },
                    };

                    _logger.LogDebug(
                        "Token authentication verified for subject {SubjectId}",
                        authContext.SubjectId
                    );
                    return Ok(response);
                }
                else
                {
                    // For API secret authentication, use simple "OK" response
                    var response = new VerifyAuthResponse { Message = "OK" };

                    _logger.LogDebug("API secret authentication verified");
                    return Ok(response);
                }
            }
            else
            {
                // Not authenticated - return unauthorized message
                var response = new VerifyAuthResponse
                {
                    Message = new AuthResponseMessage
                    {
                        RoleFound = "NOT_FOUND",
                        Message = "UNAUTHORIZED",
                        CanRead = false,
                        CanWrite = false,
                        IsAdmin = false,
                        Permissions = "",
                    },
                };

                _logger.LogDebug("Authentication verification failed - no valid credentials");
                return Ok(response);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication verification");

            var response = new VerifyAuthResponse
            {
                Message = new AuthResponseMessage
                {
                    RoleFound = "ERROR",
                    Message = "INTERNAL_ERROR",
                    CanRead = false,
                    CanWrite = false,
                    IsAdmin = false,
                    Permissions = "",
                },
            };

            return Ok(response);
        }
    }
}

/// <summary>
/// Response for the /api/v1/verifyauth endpoint
/// </summary>
public class VerifyAuthResponse
{
    /// <summary>
    /// Authentication message - can be either a string or an object
    /// </summary>
    public object Message { get; set; } = "";
}

/// <summary>
/// Detailed authentication response message
/// </summary>
public class AuthResponseMessage
{
    /// <summary>
    /// Whether the role was found (FOUND, NOT_FOUND, ERROR)
    /// </summary>
    public string RoleFound { get; set; } = "";

    /// <summary>
    /// Status message (OK, UNAUTHORIZED, INTERNAL_ERROR)
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Whether the user can read data
    /// </summary>
    public bool CanRead { get; set; }

    /// <summary>
    /// Whether the user can write data
    /// </summary>
    public bool CanWrite { get; set; }

    /// <summary>
    /// Whether the user has admin permissions
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Comma-separated list of permissions
    /// </summary>
    public string Permissions { get; set; } = "";
}
