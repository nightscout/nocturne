using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Devices;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Exposes the current reservoir level for display.
/// </summary>
/// <remarks>
/// Delegates to <see cref="IReservoirEstimationService"/>, which anchors on the newest
/// exact reservoir reading and subtracts insulin delivered since. Uses the
/// <see cref="ReservoirEstimateResponse.Found"/> flag rather than 204 so the generated
/// remote query always yields a typed body.
/// </remarks>
/// <seealso cref="IReservoirEstimationService"/>
/// <seealso cref="ReservoirReportsController"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/reservoir")]
[Authorize]
[Produces("application/json")]
public class ReservoirController(IReservoirEstimationService estimation) : ControllerBase
{
    /// <summary>
    /// Get the current reservoir estimate.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The estimate, or <c>found = false</c> when no snapshot carries a reservoir value.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(ReservoirEstimateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReservoirEstimateResponse>> GetEstimate(CancellationToken ct = default)
    {
        var estimate = await estimation.GetEstimateAsync(asOf: null, ct);

        return Ok(estimate is null
            ? new ReservoirEstimateResponse { Found = false }
            : new ReservoirEstimateResponse
            {
                Found = true,
                Units = estimate.Units,
                IsLowerBound = estimate.IsLowerBound,
                IsEstimated = estimate.IsEstimated,
            });
    }
}
