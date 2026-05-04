using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for managing time-ranged system states such as pump modes, connectivity periods,
/// temporary targets, overrides, and user-annotated activity periods (sleep, exercise, illness, travel).
/// </summary>
/// <remarks>
/// <see cref="StateSpan"/> records are created automatically by connector-based ingest pipelines
/// but can also be created and updated manually via this API.
///
/// Convenience sub-routes (<c>/pump-modes</c>, <c>/connectivity</c>, <c>/overrides</c>,
/// <c>/temporary-targets</c>, <c>/profiles</c>, <c>/sleep</c>, <c>/exercise</c>,
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

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Profile, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Profile, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get sleep state spans (user-annotated sleep periods)
    /// </summary>
    [HttpGet("sleep")]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<StateSpan>>> GetSleep(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Sleep, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Sleep, from: from, to: to, cancellationToken: cancellationToken);
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

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(StateSpanCategory.Travel, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(StateSpanCategory.Travel, from: from, to: to, cancellationToken: cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get all activity state spans (sleep, exercise, illness, travel)
    /// </summary>
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

        var descending = sort == "timestamp_desc";
        var activityCategories = new[] { StateSpanCategory.Sleep, StateSpanCategory.Exercise, StateSpanCategory.Illness, StateSpanCategory.Travel };
        var allSpans = new List<StateSpan>();
        var total = 0;

        foreach (var category in activityCategories)
        {
            var spans = await _stateSpanService.GetStateSpansAsync(category, from: from, to: to, count: int.MaxValue, descending: descending, cancellationToken: cancellationToken);
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
    public async Task<ActionResult<StateSpan>> CreateStateSpan(
        [FromBody] CreateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
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
    public async Task<ActionResult<StateSpan>> UpdateStateSpan(
        string id,
        [FromBody] UpdateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _stateSpanService.GetStateSpanByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

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
    public async Task<IActionResult> DeleteStateSpan(string id, CancellationToken cancellationToken = default)
    {
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
