using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Provides the Nightscout <c>/api/v1/experiments/test</c> endpoint used by AID apps —
/// notably Loop — to verify their API secret while adding the Nightscout service.
/// </summary>
/// <remarks>
/// Loop's connection check issues an authenticated <c>GET /api/v1/experiments/test</c> and
/// treats only a 200 as success (401 means "bad secret"). Without this endpoint Nocturne
/// returns 404, so Loop reports a network error and aborts setup even though entries and
/// devicestatus uploads succeed. Mirrors reference Nightscout, which gates the endpoint on
/// read permission and returns <c>{ status: "ok" }</c>.
/// </remarks>
[ApiController]
[Tags("V1")]
[Route("api/v1")]
public class ExperimentsController : ControllerBase
{
    /// <summary>
    /// Confirms the request carries valid, authorized credentials.
    /// </summary>
    /// <returns>200 with <c>{ status: "ok" }</c> when authorized; 401 otherwise.</returns>
    [HttpGet("experiments/test")]
    [RequireRead]
    [NightscoutEndpoint("/api/v1/experiments/test")]
    [ProducesResponseType(200)]
    [ProducesResponseType(401)]
    public IActionResult Test()
    {
        return Ok(new { status = "ok" });
    }
}
