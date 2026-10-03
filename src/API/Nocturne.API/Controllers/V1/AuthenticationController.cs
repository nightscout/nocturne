using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
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
[Tags("V1")]
[Route("api/v1")]
[AllowAnonymous]
public class AuthenticationController : ControllerBase
{
    /// <summary>
    /// Report what the request's credential may do. Answers 200 whether or not the caller is
    /// authenticated, as Nightscout does; the message says which.
    /// </summary>
    [HttpGet("verifyauth")]
    [NightscoutEndpoint("/api/v1/verifyauth")]
    [ProducesResponseType(typeof(VerifyAuthResponse), 200)]
    public ActionResult<VerifyAuthResponse> VerifyAuthentication()
    {
        var authContext = HttpContext.GetAuthContext();
        var authenticated = authContext?.IsAuthenticated == true;
        var grantedScopes = HttpContext.GetGrantedScopes();

        return Ok(new VerifyAuthResponse
        {
            Message = new VerifyAuthMessage
            {
                CanRead = ScopeTranslator.GrantsReadEverything(grantedScopes),
                CanWrite = ScopeTranslator.GrantsWriteEverything(grantedScopes),
                IsAdmin = grantedScopes.Contains(Scope.FullAccess),
                Message = authenticated ? "OK" : "UNAUTHORIZED",
                RoleFound = authenticated && authContext!.AuthType != AuthType.ApiKey ? "FOUND" : "NOTFOUND",
                Permissions = authenticated ? "ROLE" : "DEFAULT",
            },
        });
    }
}

/// <summary>
/// Nightscout's <c>sendJSONStatus</c> envelope around a <see cref="VerifyAuthMessage"/>.
/// </summary>
public class VerifyAuthResponse
{
    public int Status { get; set; } = StatusCodes.Status200OK;

    public VerifyAuthMessage Message { get; set; } = new();
}

/// <summary>
/// What the credential may do, in Nightscout's spelling. The flags are written even when false,
/// which the Nightscout JSON options would otherwise drop, because Nightscout always sends them.
/// </summary>
public class VerifyAuthMessage
{
    /// <summary>Whether the credential reads every data category, as Nightscout's <c>*:*:read</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool CanRead { get; set; }

    /// <summary>Whether the credential writes every data category, as Nightscout's <c>*:*:write</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool CanWrite { get; set; }

    /// <summary>Whether the credential holds <see cref="Scope.FullAccess"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool IsAdmin { get; set; }

    /// <summary><c>OK</c> for an authenticated caller, otherwise <c>UNAUTHORIZED</c>.</summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// <c>FOUND</c> for a token-style credential, <c>NOTFOUND</c> for an api-secret or no
    /// credential. Nightscout's web client treats <c>FOUND</c> as token authentication.
    /// </summary>
    [JsonPropertyName("rolefound")]
    public string RoleFound { get; set; } = "";

    /// <summary><c>ROLE</c> for an authenticated caller, <c>DEFAULT</c> when only the default roles apply.</summary>
    public string Permissions { get; set; } = "";
}
