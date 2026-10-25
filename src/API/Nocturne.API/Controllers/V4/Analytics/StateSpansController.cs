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
/// Every read shares the caching posture argued at <see cref="GetStateSpans"/>. <c>GET /</c>, the
/// category sub-routes and <c>GET /{id}</c> are annotated with <c>RemoteQueryAttribute</c>;
/// create, update, and delete use <c>RemoteCommandAttribute</c> with cache invalidation hints.
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
    /// <remarks>
    /// <para>
    /// Narrowed to the categories the caller may read (<see cref="StateSpanReadScopeGuard"/>):
    /// asking for one it may not read is refused, and leaving the category out reads every one it
    /// may. <see cref="GetStateSpan"/> answers 404 for a span of a category the caller may not read.
    /// </para>
    /// <para>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a temporary
    /// target, override, or data-exclusion window the caller just set must not be invisible until a
    /// cached list body expires — and an exclusion window decides whether readings count towards
    /// analytics, so a stale one silently changes reported statistics.
    /// </para>
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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

        var granted = HttpContext.GetGrantedScopes();
        if (category is { } requested && !StateSpanReadScopeGuard.CanRead(granted, requested))
            return this.ForbiddenForScope(StateSpanReadScopeGuard.RequiredReadScope(requested));

        IReadOnlyCollection<StateSpanCategory> categories = category is { } only
            ? [only]
            : StateSpanReadScopeGuard.ReadableCategories(granted);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansInCategoriesAsync(
            categories, state, from, to, source, active, limit, offset, descending, cancellationToken);
        var total = await _stateSpanService.CountStateSpansInCategoriesAsync(
            categories, state, from, to, source, active, cancellationToken);
        return Ok(new PaginatedResponse<StateSpan> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get pump mode state spans
    /// </summary>
    [HttpGet("pump-modes")]
    [RequireScope(Scope.DevicesRead)]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginatedResponse<StateSpan>>> GetPumpModes(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
        => CategoryPage(StateSpanCategory.PumpMode, from, to, limit, offset, sort, cancellationToken);

    /// <summary>
    /// Get connectivity state spans
    /// </summary>
    [HttpGet("connectivity")]
    [RequireScope(Scope.DevicesRead)]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginatedResponse<StateSpan>>> GetConnectivity(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
        => CategoryPage(StateSpanCategory.PumpConnectivity, from, to, limit, offset, sort, cancellationToken);

    /// <summary>
    /// Get override state spans
    /// </summary>
    [HttpGet("overrides")]
    [RequireScope(Scope.TreatmentsRead)]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginatedResponse<StateSpan>>> GetOverrides(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
        => CategoryPage(StateSpanCategory.Override, from, to, limit, offset, sort, cancellationToken);

    /// <summary>
    /// Get temporary target state spans (AAPS temporary glucose targets)
    /// </summary>
    [HttpGet("temporary-targets")]
    [RequireScope(Scope.TreatmentsRead)]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginatedResponse<StateSpan>>> GetTemporaryTargets(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
        => CategoryPage(StateSpanCategory.TemporaryTarget, from, to, limit, offset, sort, cancellationToken);

    /// <summary>
    /// Get exercise state spans (user-annotated activity periods)
    /// </summary>
    [HttpGet("exercise")]
    [RequireScope(Scope.TreatmentsRead)]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginatedResponse<StateSpan>>> GetExercise(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
        => CategoryPage(StateSpanCategory.Exercise, from, to, limit, offset, sort, cancellationToken);

    /// <summary>
    /// Get illness state spans (user-annotated illness periods)
    /// </summary>
    [HttpGet("illness")]
    [RequireScope(Scope.TreatmentsRead)]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginatedResponse<StateSpan>>> GetIllness(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
        => CategoryPage(StateSpanCategory.Illness, from, to, limit, offset, sort, cancellationToken);

    /// <summary>
    /// Get travel state spans (user-annotated travel/timezone change periods)
    /// </summary>
    [HttpGet("travel")]
    [RequireScope(Scope.TreatmentsRead)]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<StateSpan>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginatedResponse<StateSpan>>> GetTravel(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
        => CategoryPage(StateSpanCategory.Travel, from, to, limit, offset, sort, cancellationToken);

    /// <summary>
    /// One category's page, as every category sub-route returns it.
    /// </summary>
    private async Task<ActionResult<PaginatedResponse<StateSpan>>> CategoryPage(
        StateSpanCategory category,
        DateTime? from,
        DateTime? to,
        int limit,
        int offset,
        string sort,
        CancellationToken cancellationToken)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await _stateSpanService.GetStateSpansAsync(category, from: from, to: to, count: limit, skip: offset, descending: descending, cancellationToken: cancellationToken);
        var total = await _stateSpanService.CountStateSpansAsync(category, from: from, to: to, cancellationToken: cancellationToken);
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
    [RequireScope(Scope.TreatmentsRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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
        var allSpans = new List<StateSpan>();
        var total = 0;

        // Every span on the requested page is within the first offset + limit of its own category,
        // a sum ClampMergedPage has already held inside the merged window — or driven to a zero
        // limit, a page no category has to be read for at all.
        var perCategoryWindow = limit == 0 ? 0 : offset + limit;

        foreach (var category in StateSpanCategories.DrawnActivities)
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
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status200OK)]
    public async Task<ActionResult<StateSpan>> GetStateSpan(string id, CancellationToken cancellationToken = default)
    {
        var span = await _stateSpanService.GetStateSpanByIdAsync(id, cancellationToken);
        if (span == null || !StateSpanReadScopeGuard.CanRead(HttpContext.GetGrantedScopes(), span.Category))
            return NotFound();
        return Ok(span);
    }

    /// <summary>
    /// Create a new state span (manual entry)
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = [
        nameof(GetStateSpans),
        nameof(GetPumpModes), nameof(GetConnectivity), nameof(GetOverrides), nameof(GetTemporaryTargets),
        nameof(GetExercise), nameof(GetIllness), nameof(GetTravel)])]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StateSpan>> CreateStateSpan(
        [FromBody] CreateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Category == StateSpanCategory.Profile)
            return ProfileSpanRefused();

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
    [RemoteCommand(Invalidates = [
        nameof(GetStateSpans), nameof(GetStateSpan),
        nameof(GetPumpModes), nameof(GetConnectivity), nameof(GetOverrides), nameof(GetTemporaryTargets),
        nameof(GetExercise), nameof(GetIllness), nameof(GetTravel)])]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StateSpan>> UpdateStateSpan(
        string id,
        [FromBody] UpdateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Category == StateSpanCategory.Profile)
            return ProfileSpanRefused();

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
    [RemoteCommand(Invalidates = [
        nameof(GetStateSpans), nameof(GetStateSpan),
        nameof(GetPumpModes), nameof(GetConnectivity), nameof(GetOverrides), nameof(GetTemporaryTargets),
        nameof(GetExercise), nameof(GetIllness), nameof(GetTravel)])]
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

    /// <summary>
    /// Restore a soft-deleted state span by the ID reads return for it
    /// </summary>
    /// <remarks>
    /// Returns <c>404 Not Found</c> when no soft-deleted span has the ID. A span is refused with
    /// <c>409 Conflict</c>, and stays deleted, when a live span holds its original id at the time of
    /// the restore. A span that another span superseded before the delete is not re-pointed at.
    /// </remarks>
    [HttpPost("{id}/restore")]
    [RemoteCommand(Invalidates = [
        nameof(GetStateSpans), nameof(GetStateSpan),
        nameof(GetPumpModes), nameof(GetConnectivity), nameof(GetOverrides), nameof(GetTemporaryTargets),
        nameof(GetExercise), nameof(GetIllness), nameof(GetTravel)])]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StateSpan>> RestoreStateSpan(string id, CancellationToken cancellationToken = default)
    {
        var deleted = await _stateSpanService.ResolveDeletedAsync([id], cancellationToken);
        if (deleted.Count == 0)
            return NotFound();

        var (rowId, (_, category)) = deleted.Single();
        var missingScope = StateSpanWriteScopeGuard.FindMissingScope(HttpContext.GetGrantedScopes(), category);
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

        try
        {
            return Ok(await _stateSpanService.RestoreStateSpanAsync(rowId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Restore soft-deleted state spans by the IDs reads return for them
    /// </summary>
    /// <remarks>
    /// IDs that match no soft-deleted span are ignored. A span left deleted because a live span holds
    /// its original id is listed in <c>conflicts</c> by the ID sent; the rest of the batch is still
    /// restored. The caller needs write access to the category of every span restored.
    /// </remarks>
    [HttpPost("restore")]
    [RemoteCommand(Invalidates = [
        nameof(GetStateSpans), nameof(GetStateSpan),
        nameof(GetPumpModes), nameof(GetConnectivity), nameof(GetOverrides), nameof(GetTemporaryTargets),
        nameof(GetExercise), nameof(GetIllness), nameof(GetTravel)])]
    [ProducesResponseType(typeof(StateSpanBulkRestoreResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StateSpanBulkRestoreResult>> BulkRestoreStateSpans(
        [FromBody] string[] ids, CancellationToken cancellationToken = default)
    {
        var deleted = await _stateSpanService.ResolveDeletedAsync(ids, cancellationToken);
        var missingScope = StateSpanWriteScopeGuard.FindMissingScope(
            HttpContext.GetGrantedScopes(), deleted.Values.Select(d => d.Category).Distinct().ToArray());
        if (missingScope is not null)
            return this.ForbiddenForScope(missingScope);

        var result = await _stateSpanService.BulkRestoreStateSpansAsync(deleted.Keys, cancellationToken);
        return Ok(new StateSpanBulkRestoreResult
        {
            Restored = result.Restored,
            Conflicts = result.Conflicts.Select(rowId => deleted[rowId].SentId).ToList(),
        });
    }

    /// <summary>
    /// No <see cref="StateSpanCategory.Profile"/> span is written: a profile switch is a
    /// <see cref="Core.Models.V4.ProfileActivation"/>, written as a Profile Switch treatment.
    /// </summary>
    private ObjectResult ProfileSpanRefused() => Problem(
        detail: "Profile switches are profile activations, read at /api/v4/profile/activations and written as Profile Switch treatments.",
        statusCode: StatusCodes.Status400BadRequest,
        title: "Bad Request");
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
