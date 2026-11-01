using Nocturne.API.Services.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for activity data including exercise, heart rate, and step count records.
/// </summary>
/// <remarks>
/// This endpoint merges several data categories: writes are routed by record content to the
/// heart-rate, step-count, sleep, or state-span tables. Writes are gated in two layers — a baseline
/// <c>RequireScope</c> admits the caller, and the destination's own category scope is then enforced
/// per record via <see cref="ActivityWriteScopeGuard"/>, because the destination is not known until
/// the body has been read.
/// <para>
/// Create and update admit a caller holding <c>treatments.readwrite</c>, the category a
/// StateSpan-backed activity writes under, or <c>sleep.readwrite</c>, the category a sleep-typed
/// activity writes under; <see cref="IActivityDecomposer.RequiredWriteScope"/> then names the
/// scope of each record. Update and delete also check every record the id already names
/// (<see cref="IActivityService.GetStoredActivitiesAsync"/>). Delete admits
/// <c>treatments.readwrite</c> only.
/// </para>
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
    /// <remarks>
    /// This read merges four sources in memory, so the page is bounded by
    /// <see cref="V4ReadLimits.ClampMergedPage"/> rather than the plain page-size ceiling.
    /// The scope requirement is an OR over the four storages, and
    /// <see cref="ActivityReadScopeGuard"/> then drops the records whose category the caller does
    /// not hold — pagination happens in the service before that filter, so a caller holding a
    /// subset of the categories can receive fewer than <paramref name="limit"/> records. The total
    /// is counted over only those same categories, both so it stays consistent with what the page
    /// can contain and so it cannot disclose how many records exist in a category the caller may
    /// not read.
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<Activity>), StatusCodes.Status200OK)]
    [RequireScope(
        Scope.TreatmentsRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead)]
    public async Task<ActionResult<PaginatedResponse<Activity>>> GetActivities(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        offset = V4ReadLimits.ClampOffset(offset);
        limit = V4ReadLimits.ClampMergedPage(limit, offset);

        var records = await _activityService.GetActivitiesAsync(
            count: limit, skip: offset, cancellationToken: cancellationToken);
        var visible = ActivityReadScopeGuard.Filter(
            records, _activityDecomposer, HttpContext.GetGrantedScopes());

        var counts = await _activityService.CountActivitiesByCategoryAsync(
            ActivityReadScopeGuard.GrantedCategories(HttpContext),
            cancellationToken: cancellationToken);
        var total = (int)counts.Values.Sum();

        return Ok(new PaginatedResponse<Activity>
        {
            Data = visible,
            Pagination = new PaginationInfo(limit, offset, total),
        });
    }

    /// <summary>
    /// Get a specific activity record by ID
    /// </summary>
    /// <remarks>
    /// A record in a category the caller does not hold answers 404 rather than 403, so the
    /// response does not disclose that the record exists.
    /// </remarks>
    [HttpGet("{id}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(Activity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequireScope(
        Scope.TreatmentsRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead)]
    public async Task<ActionResult<Activity>> GetActivity(
        string id,
        CancellationToken cancellationToken = default)
    {
        var record = ActivityReadScopeGuard.FirstReadable(
            await _activityService.GetStoredActivitiesAsync(id, cancellationToken),
            _activityDecomposer,
            HttpContext.GetGrantedScopes());
        if (record == null)
            return NotFound();

        return Ok(record);
    }

    /// <summary>
    /// Create one or more activity records
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.TreatmentsReadWrite, Scope.SleepReadWrite)]
    [ProducesResponseType(typeof(IEnumerable<Activity>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<Activity>>> CreateActivities(
        [FromBody] UpsertActivityRequest[] requests,
        CancellationToken cancellationToken = default)
    {
        if (this.ValidateBulkSize(requests, "Activity", "activity records") is { } invalid)
            return invalid;

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
    [RequireScope(Scope.TreatmentsReadWrite, Scope.SleepReadWrite)]
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
        var toCheck = new List<Activity> { activity };
        toCheck.AddRange(await _activityService.GetStoredActivitiesAsync(id, cancellationToken));
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
    [RequireScope(Scope.TreatmentsReadWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteActivity(
        string id,
        CancellationToken cancellationToken = default)
    {
        var missingScope = ActivityWriteScopeGuard.FindMissingScope(
            await _activityService.GetStoredActivitiesAsync(id, cancellationToken),
            _activityDecomposer,
            HttpContext.GetGrantedScopes());
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

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
