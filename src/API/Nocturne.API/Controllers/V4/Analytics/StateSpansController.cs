using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for managing time-ranged system states such as pump modes, connectivity periods,
/// temporary targets, overrides, and user-annotated activity periods (exercise, illness, travel).
/// </summary>
/// <remarks>
/// <see cref="StateSpan"/> records are created automatically by connector-based ingest pipelines
/// but can also be created and updated manually via this API.
///
/// Convenience sub-routes (<c>/pump-modes</c>, <c>/connectivity</c>, <c>/overrides</c>,
/// <c>/temporary-targets</c>, <c>/profiles</c>, <c>/exercise</c>,
/// <c>/illness</c>, <c>/travel</c>, <c>/activities</c>) are thin wrappers that pre-filter
/// <see cref="IStateSpanService.GetStateSpansAsync"/> by <see cref="StateSpanCategory"/>.
///
/// The main <c>GET /</c> endpoint is annotated with <c>RemoteQueryAttribute</c>
/// and cached for 120 seconds. Create, update, and delete use
/// <c>RemoteCommandAttribute</c> with cache invalidation hints.
/// </remarks>
/// <seealso cref="IStateSpanService"/>
/// <seealso cref="StateSpan"/>
/// <seealso cref="StateSpanCategory"/>
[ApiController]
[Tags("State Spans")]
[Route("api/v4/state-spans")]
[Authorize]
public class StateSpansController : ControllerBase
{
    // Writes are gated per record by StateSpanWriteScopeGuard rather than by a single declared
    // controller scope. state_spans is not in ShareDataCategories.GovernedTables and holds four
    // different data categories behind one table: the caller picks which by setting
    // StateSpan.Category in the body, so the required scope is not known until the body is read.
    // A flat treatments.readwrite would let a treatments-only credential write PumpMode and
    // PumpConnectivity spans (devices), Profile switches (therapy), and DataExclusion windows —
    // which decide whether glucose readings count towards analytics and reports, so excluding one
    // can hide a hypo. The class-level [Authorize] alone is satisfied by read-only credentials
    // such as a guest-link session, which is what this closes.

    private readonly IStateSpanService _stateSpanService;

    public StateSpansController(IStateSpanService stateSpanService)
    {
        _stateSpanService = stateSpanService;
    }

    /// <summary>
    /// Query all state spans with optional filtering
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ResponseCache(Duration = 120, VaryByQueryKeys = new[] { "*" })]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetStateSpans(
        [FromQuery] StateSpanCategory? category = null,
        [FromQuery] string? state = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? source = null,
        [FromQuery] bool? active = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(
            category, state, from, to, source, active, limit, offset, descending, cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(
            category, state, from, to, source, active, cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get pump mode state spans
    /// </summary>
    [HttpGet("pump-modes")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetPumpModes(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.PumpMode, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.PumpMode, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get connectivity state spans
    /// </summary>
    [HttpGet("connectivity")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetConnectivity(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.PumpConnectivity, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.PumpConnectivity, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get override state spans
    /// </summary>
    [HttpGet("overrides")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetOverrides(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Override, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Override, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get temporary target state spans (AAPS temporary glucose targets)
    /// </summary>
    [HttpGet("temporary-targets")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetTemporaryTargets(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.TemporaryTarget, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.TemporaryTarget, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get profile state spans
    /// </summary>
    [HttpGet("profiles")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetProfiles(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Profile, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Profile, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get exercise state spans (user-annotated activity periods)
    /// </summary>
    [HttpGet("exercise")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetExercise(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Exercise, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Exercise, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get illness state spans (user-annotated illness periods)
    /// </summary>
    [HttpGet("illness")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetIllness(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Illness, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Illness, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get travel state spans (user-annotated travel/timezone change periods)
    /// </summary>
    [HttpGet("travel")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetTravel(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Travel, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Travel, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get all activity state spans (exercise, illness, travel)
    /// </summary>
    /// <remarks>
    /// This read merges three categories in memory before it paginates, so the page is bounded by
    /// <see cref="V4ReadLimits.ClampMergedPage"/> rather than the plain page-size ceiling, and each
    /// category is fetched only as deep as the requested page reaches.
    /// </remarks>
    [HttpGet("activities")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetActivities(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        offset = V4ReadLimits.ClampOffset(offset);
        limit = V4ReadLimits.ClampMergedPage(limit, offset);

        var descending = sort == "timestamp_desc";
        var activityCategories = new[] { StateSpanCategory.Exercise, StateSpanCategory.Illness, StateSpanCategory.Travel };
        var allSpans = new List<StateSpan>();
        var total = 0;

        // Every span on the requested page is within the first offset + limit of its own category,
        // a sum ClampMergedPage has already held inside the merged window — or driven to a zero
        // limit, a page no category has to be read for at all.
        var perCategoryWindow = limit == 0 ? 0 : offset + limit;

        foreach (var category in activityCategories)
        {
            var spans = await _stateSpanService.GetStateSpansAsync(category, from: from, to: to, count: perCategoryWindow, descending: descending, cancellationToken: cancellationToken);
            allSpans.AddRange(spans);
            total += await _stateSpanService.CountStateSpansAsync(category, from: from, to: to, cancellationToken: cancellationToken);
        }

        var ordered = descending
            ? allSpans.OrderByDescending(s => s.StartMills)
            : allSpans.OrderBy(s => s.StartMills);

        var paged = ordered.Skip(offset).Take(limit).ToList();
        return Ok(new PaginatedResponse<StateSpan> { Data = paged, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get a specific state span by ID
    /// </summary>
    [HttpGet("{id}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status200OK)]
    public async Task<ActionResult<StateSpan>> GetStateSpan(string id, CancellationToken cancellationToken = default)
    {
        var span = await _stateSpanService.GetStateSpanByIdAsync(id, cancellationToken);
        if (span == null)
            return NotFound();
        return Ok(span);
    }

    /// <summary>
    /// Create a new state span (manual entry)
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetStateSpans"])]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StateSpan>> CreateStateSpan(
        [FromBody] CreateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        var missingScope = StateSpanWriteScopeGuard.FindMissingScope(
            HttpContext.GetGrantedScopes(), request.Category);
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

        var stateSpan = new StateSpan
        {
            Category = request.Category,
            State = request.State,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(request.StartMills).UtcDateTime,
            EndTimestamp = request.EndMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(request.EndMills.Value).UtcDateTime : null,
            Source = request.Source ?? "manual",
            Metadata = request.Metadata,
            OriginalId = request.OriginalId,
        };

        var created = await _stateSpanService.UpsertStateSpanAsync(stateSpan, cancellationToken);
        return CreatedAtAction(nameof(GetStateSpan), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing state span
    /// </summary>
    [HttpPut("{id}")]
    [RemoteCommand(Invalidates = ["GetStateSpans", "GetStateSpan"])]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StateSpan>> UpdateStateSpan(
        string id,
        [FromBody] UpdateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _stateSpanService.GetStateSpanByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        // Both categories: moving a span out of one category and into another needs write access to
        // each, or a caller could relocate a record into a category it may not write.
        var missingScope = StateSpanWriteScopeGuard.FindMissingScope(
            HttpContext.GetGrantedScopes(), existing.Category, request.Category ?? existing.Category);
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

        var updated = new StateSpan
        {
            Id = existing.Id,
            Category = request.Category ?? existing.Category,
            State = request.State ?? existing.State,
            StartTimestamp = request.StartMills.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(request.StartMills.Value).UtcDateTime
                : existing.StartTimestamp,
            EndTimestamp = request.EndMills.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(request.EndMills.Value).UtcDateTime
                : existing.EndTimestamp,
            Source = request.Source ?? existing.Source,
            Metadata = request.Metadata ?? existing.Metadata,
            OriginalId = existing.OriginalId,
        };

        var result = await _stateSpanService.UpdateStateSpanAsync(id, updated, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Delete a state span
    /// </summary>
    [HttpDelete("{id}")]
    [RemoteCommand(Invalidates = ["GetStateSpans"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteStateSpan(string id, CancellationToken cancellationToken = default)
    {
        // The stored record's own category decides the scope, so the span has to be read first.
        var existing = await _stateSpanService.GetStateSpanByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        var missingScope = StateSpanWriteScopeGuard.FindMissingScope(
            HttpContext.GetGrantedScopes(), existing.Category);
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

        var deleted = await _stateSpanService.DeleteStateSpanAsync(id, cancellationToken);
        if (!deleted)
            return NotFound();
        return NoContent();
    }
}

#region Request Models

public class CreateStateSpanRequest
{
    public StateSpanCategory Category { get; set; }
    public string? State { get; set; }
    public long StartMills { get; set; }
    public long? EndMills { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string? OriginalId { get; set; }
}

public class UpdateStateSpanRequest
{
    public StateSpanCategory? Category { get; set; }
    public string? State { get; set; }
    public long? StartMills { get; set; }
    public long? EndMills { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

#endregion
