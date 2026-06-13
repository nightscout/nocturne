using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Models.Authorization;

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
/// read access and returns <c>{ status: "ok" }</c>.
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
    /// <remarks>
    /// Gated on read access to any core Nightscout data type (matched via scopes, like the
    /// rest of v1). Using <see cref="RequireScopeAttribute"/> rather than a broad
    /// <c>RequireRead</c> permission means scoped uploader tokens (e.g. a Loop key with
    /// <c>glucose.readwrite</c>) pass, instead of only full-access/legacy <c>*</c> secrets.
    /// </remarks>
    [HttpGet("experiments/test")]
    [RequireScope(OAuthScopes.GlucoseRead, OAuthScopes.TreatmentsRead, OAuthScopes.DevicesRead)]
    [NightscoutEndpoint("/api/v1/experiments/test")]
    [ProducesResponseType(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public IActionResult Test()
    {
        return Ok(new { status = "ok" });
    }
}
