using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// The instance's registered Google redirect URI. It sits under <c>/api</c> so the gateway
/// routes it to the API, where <see cref="Middleware.ApexCallbackRelayMiddleware"/> can move an
/// apex hit onto its tenant host; it then hands the browser to the web route that completes the
/// exchange with the member's session. It does nothing else, so it needs no authentication.
/// </summary>
[ApiController, AllowAnonymous, ApiExplorerSettings(IgnoreApi = true)]
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
