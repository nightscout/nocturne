using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for activity data including exercise, heart rate, and step count records.
/// </summary>
/// <remarks>
/// This endpoint merges several data categories: writes are routed by record content to the
/// heart-rate, step-count, sleep, or state-span tables. Because the destination varies per record,
/// the category write scope is enforced per record via <see cref="ActivityWriteScopeGuard"/> rather
/// than a single <c>RequireScope</c> attribute.
/// </remarks>
[ApiController]
[Tags("Health")]
[Route("api/v4/[controller]")]
[Authorize]
[Produces("application/json")]
public class ActivityController : ControllerBase
{
    private readonly IActivityService _activityService;
    private readonly IActivityDecomposer _activityDecomposer;

    public ActivityController(IActivityService activityService, IActivityDecomposer activityDecomposer)
    {
        _activityService = activityService;
        _activityDecomposer = activityDecomposer;
    }

    /// <summary>
    /// Get activity records with pagination
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<Activity>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<Activity>>> GetActivities(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var records = await _activityService.GetActivitiesAsync(
            count: limit, skip: offset, cancellationToken: cancellationToken);
        var total = (int)await _activityService.CountActivitiesAsync(cancellationToken: cancellationToken);

        return Ok(new PaginatedResponse<Activity>
        {
            Data = records,
            Pagination = new PaginationInfo(limit, offset, total),
        });
    }

    /// <summary>
    /// Get a specific activity record by ID
    /// </summary>
    [HttpGet("{id}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(Activity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Activity>> GetActivity(
        string id,
        CancellationToken cancellationToken = default)
    {
        var record = await _activityService.GetActivityByIdAsync(id, cancellationToken);
        if (record == null)
            return NotFound();

        return Ok(record);
    }

    /// <summary>
    /// Create one or more activity records
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(IEnumerable<Activity>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<Activity>>> CreateActivities(
        [FromBody] UpsertActivityRequest[] requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Length == 0)
            return BadRequest("At least one activity record is required");

        var activityList = requests.Select(MapToActivity).ToList();

        var missingScope = ActivityWriteScopeGuard.FindMissingScope(
            activityList, _activityDecomposer, HttpContext.GetGrantedScopes());
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

        var result = await _activityService.CreateActivitiesAsync(activityList, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>
    /// Update an existing activity record
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Activity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Activity>> UpdateActivity(
        string id,
        [FromBody] UpsertActivityRequest request,
        CancellationToken cancellationToken = default)
    {
        var activity = MapToActivity(request);

        // Gate on both what is being written (the payload's destination) and what is being
        // modified (the existing record's destination) — updating an exercise record into a
        // sleep one, or editing a sleep session addressed by its id, both need sleep.readwrite.
        var existing = await _activityService.GetActivityByIdAsync(id, cancellationToken);
        var toCheck = new List<Activity> { activity };
        if (existing is not null)
            toCheck.Add(existing);
        var missingScope = ActivityWriteScopeGuard.FindMissingScope(
            toCheck, _activityDecomposer, HttpContext.GetGrantedScopes());
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

        var updated = await _activityService.UpdateActivityAsync(id, activity, cancellationToken);
        if (updated == null)
            return NotFound();

        return Ok(updated);
    }

    /// <summary>
    /// Delete an activity record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteActivity(
        string id,
        CancellationToken cancellationToken = default)
    {
        // Resolve the target first so the delete is gated by the category it will remove
        // (sleep/heart-rate/step). A regular activity or a missing record needs no category scope.
        var existing = await _activityService.GetActivityByIdAsync(id, cancellationToken);
        if (existing is not null)
        {
            var missingScope = ActivityWriteScopeGuard.FindMissingScope(
                [existing], _activityDecomposer, HttpContext.GetGrantedScopes());
            if (missingScope is not null)
                return this.ForbiddenForScope(missingScope);
        }

        var deleted = await _activityService.DeleteActivityAsync(id, cancellationToken);
        if (!deleted)
            return NotFound();

        return NoContent();
    }

    private static Activity MapToActivity(UpsertActivityRequest request) => new()
    {
        Mills = request.Mills,
        UtcOffset = request.UtcOffset,
        Type = request.Type,
        Description = request.Description,
        Duration = request.Duration,
        Intensity = request.Intensity,
        Notes = request.Notes,
        EnteredBy = request.EnteredBy,
        Distance = request.Distance,
        DistanceUnits = request.DistanceUnits,
        Energy = request.Energy,
        EnergyUnits = request.EnergyUnits,
        Name = request.Name,
    };
}
