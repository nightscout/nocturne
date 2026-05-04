using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Models.CoachMarks;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing per-user coach mark progression.
/// </summary>
/// <remarks>
/// Coach marks track first-time user experience hints. Each mark transitions through
/// <c>unseen → seen → completed/dismissed</c> with server-managed timestamps.
/// </remarks>
/// <seealso cref="ICoachMarkService"/>
/// <seealso cref="CoachMarkState"/>
[ApiController]
[Route("api/v4/coach-marks")]
[Tags("Coach Marks")]
[Authorize]
public class CoachMarkController : ControllerBase
{
    private readonly ICoachMarkService _coachMarkService;

    /// <summary>
    /// Initializes a new instance of <see cref="CoachMarkController"/>.
    /// </summary>
    /// <param name="coachMarkService">Service for coach mark state persistence.</param>
    public CoachMarkController(ICoachMarkService coachMarkService)
    {
        _coachMarkService = coachMarkService;
    }

    /// <summary>
    /// Get all coach mark states for the current user.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    public async Task<ActionResult<IReadOnlyList<CoachMarkState>>> GetAll(
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetSubjectId() is null) return Ok(Array.Empty<CoachMarkState>());
        var states = await _coachMarkService.GetAllAsync(cancellationToken);
        return Ok(states);
    }

    /// <summary>
    /// Update a coach mark's status.
    /// </summary>
    /// <param name="key">The coach mark key to update.</param>
    /// <param name="request">The new status value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPatch("{key}")]
    [RemoteCommand]
    public async Task<ActionResult<CoachMarkState>> UpdateStatus(
        string key,
        [FromBody] UpdateCoachMarkRequest request,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetSubjectId() is null) return Unauthorized();
        var state = await _coachMarkService.UpsertAsync(key, request.Status, cancellationToken);
        return Ok(state);
    }

    /// <summary>
    /// Delete all coach mark states for the current user, resetting all tutorials.
    /// </summary>
    [HttpDelete]
    [RemoteCommand(Invalidates = ["GetAll"])]
    public async Task<ActionResult> DeleteAll(CancellationToken cancellationToken)
    {
        if (HttpContext.GetSubjectId() is null) return Unauthorized();
        await _coachMarkService.DeleteAllAsync(cancellationToken);
        return NoContent();
    }
}

/// <summary>
/// Request body for updating a coach mark's status.
/// </summary>
/// <param name="Status">The target status (seen, completed, dismissed).</param>
public record UpdateCoachMarkRequest(string Status);
