using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using Nocturne.API.Services.Auth;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// The two endpoints Apple calls: the redirect URI that receives the code as a form POST, and
/// the server-to-server notification endpoint. Both are registered once, on the apex, in the
/// Apple Developer account.
/// </summary>
/// <seealso cref="OidcController"/>
/// <seealso cref="AppleNotificationService"/>
[ApiController]
[Route("api/auth/oidc/apple")]
[ApiExplorerSettings(IgnoreApi = true)]
public class AppleSignInController(
    IOidcAuthService authService,
    AppleNotificationService notifications,
    ILogger<AppleSignInController> logger) : ControllerBase
{
    /// <summary>
    /// Receives Apple's form POST and answers with a 303 to the GET callback of the flow the state
    /// was issued for.
    /// </summary>
    /// <remarks>See <see cref="IOidcAuthService.GetFormPostRelayUrlAsync"/> for why it is a GET.</remarks>
    [HttpPost("callback")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Callback(
        [FromForm] string? code,
        [FromForm] string? state,
        [FromForm] string? error,
        [FromForm] string? user)
    {
        var relayUrl = string.IsNullOrEmpty(state)
            ? null
            : await authService.GetFormPostRelayUrlAsync(state, code, error, user);

        if (relayUrl is null)
        {
            logger.LogWarning("Refused an Apple callback whose state this instance did not issue");
            return Redirect(
                $"/auth/error?error=invalid_state&description={Uri.EscapeDataString("Sign-in could not be verified")}");
        }

        Response.Headers.Location = relayUrl;
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>Receives one Sign in with Apple server-to-server notification.</summary>
    /// <response code="200">The notification was verified and handled.</response>
    /// <response code="400">The notification failed verification.</response>
    [HttpPost("notifications")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [Consumes("application/json")]
    public async Task<IActionResult> Notifications([FromBody] AppleNotificationRequest request, CancellationToken ct) =>
        !string.IsNullOrEmpty(request.Payload) && await notifications.HandleAsync(request.Payload, ct)
            ? Ok()
            : BadRequest();
}

/// <summary>The body Apple posts to the notification endpoint.</summary>
public sealed class AppleNotificationRequest
{
    /// <summary>The signed notification, a JWS.</summary>
    public string? Payload { get; set; }
}
