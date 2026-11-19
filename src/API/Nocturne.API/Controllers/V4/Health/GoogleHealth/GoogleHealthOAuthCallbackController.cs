using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Receives the browser after Google Health sign-in and forwards it to the web app, which completes
/// the connection with the member's session. The endpoint needs no authentication and does nothing else.
/// </summary>
/// <remarks>
/// This is the instance's registered Google redirect URI. It sits under <c>/api</c> so the gateway
/// routes it to the API, where <see cref="Middleware.ApexCallbackRelayMiddleware"/> can move an
/// apex hit onto its tenant host.
/// </remarks>
[ApiController, AllowAnonymous, ApiExplorerSettings(IgnoreApi = true)]
[Tags("Connectors")]
public class GoogleHealthOAuthCallbackController : ControllerBase
{
    public const string WebCallbackPath = "/settings/connectors/google-health/callback";

    [HttpGet(GoogleHealthPlatformOptions.CallbackPath)]
    public IActionResult RelayGoogleHealthCallback()
    {
        Response.Headers.Location = WebCallbackPath + Request.QueryString;
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return StatusCode(StatusCodes.Status303SeeOther);
    }
}
