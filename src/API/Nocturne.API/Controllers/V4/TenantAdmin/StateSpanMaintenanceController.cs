using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Admin maintenance over stored state spans. Sits beside deduplication: both reconcile what
/// several imports left behind, here a device's mode reported as many short same-state spans.
/// </summary>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/admin/state-spans")]
[Produces("application/json")]
[RequireAdmin]
public class StateSpanMaintenanceController(
    IStateSpanService stateSpanService,
    ILogger<StateSpanMaintenanceController> logger) : ControllerBase
{
    /// <inheritdoc cref="IStateSpanService.FoldStoredSpansAsync"/>
    [HttpPost("fold")]
    [RemoteCommand]
    [ProducesResponseType(typeof(StateSpanFoldResult), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<StateSpanFoldResult>> FoldStoredSpans(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await stateSpanService.FoldStoredSpansAsync(cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fold stored state spans");
            return Problem(detail: "Failed to fold state spans", statusCode: 500, title: "Internal Server Error");
        }
    }
}
