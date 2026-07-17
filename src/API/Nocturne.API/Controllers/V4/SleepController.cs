using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing sleep sessions recorded by wearables or health platforms.
/// </summary>
/// <remarks>
/// Sleep sessions include time-bounded sleep periods with optional stage intervals
/// and biometric samples. The list endpoint returns sessions without stages/biometrics
/// for efficiency; the detail endpoint includes them.
/// </remarks>
/// <seealso cref="ISleepService"/>
/// <seealso cref="SleepSession"/>
[ApiController]
[Tags("Sleep")]
[Route("api/v4/sleep/sessions")]
[Authorize]
public class SleepController : ControllerBase
{
    private readonly ISleepService _sleepService;

    public SleepController(ISleepService sleepService)
    {
        _sleepService = sleepService;
    }

    /// <summary>
    /// Query sleep sessions with optional filtering (stages and biometrics excluded)
    /// </summary>
    [HttpGet]
    [RequireScope(OAuthScopes.SleepRead)]
    [ProducesResponseType(typeof(PaginatedResponse<SleepSession>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<SleepSession>>> GetSessions(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] SleepSessionType? type = null,
        [FromQuery] SleepSource? source = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        CancellationToken cancellationToken = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        var descending = sort == "timestamp_desc";
        var data = await _sleepService.GetSessionsAsync(from, to, type, source, limit, offset, descending, cancellationToken: cancellationToken);
        var total = await _sleepService.CountSessionsAsync(from, to, type, source, cancellationToken);
        return Ok(new PaginatedResponse<SleepSession> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    /// <summary>
    /// Get a sleep session by ID (includes stages and biometric samples)
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequireScope(OAuthScopes.SleepRead)]
    [ProducesResponseType(typeof(SleepSession), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SleepSession>> GetSession(Guid id, CancellationToken cancellationToken = default)
    {
        var session = await _sleepService.GetSessionByIdAsync(id, cancellationToken);
        if (session == null)
            return NotFound();
        return Ok(session);
    }

    /// <summary>
    /// Create or upsert a sleep session
    /// </summary>
    [HttpPost]
    [RequireScope(OAuthScopes.SleepReadWrite)]
    [ProducesResponseType(typeof(SleepSession), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SleepSession>> CreateSession(
        [FromBody] SleepSession session,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var created = await _sleepService.UpsertSessionAsync(session, cancellationToken);
            return CreatedAtAction(nameof(GetSession), new { id = created.Id }, created);
        }
        catch (DbUpdateException)
        {
            // A concurrent request inserted a session with the same key between
            // the upsert's dedup lookup and its insert.
            return Problem(detail: "A sleep session with the same identifier was created concurrently.", statusCode: 409, title: "Conflict");
        }
    }

    /// <summary>
    /// Create or upsert sleep sessions in bulk (max 100)
    /// </summary>
    /// <remarks>
    /// Sessions are upserted one by one with the same dedup semantics as the single create, so a
    /// retried batch is idempotent. On a concurrent-insert conflict the request stops with `409
    /// Conflict`; sessions upserted before the conflict remain persisted, and retrying the whole
    /// batch is safe.
    /// </remarks>
    [HttpPost("bulk")]
    [RequireScope(OAuthScopes.SleepReadWrite)]
    [ProducesResponseType(typeof(SleepSession[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SleepSession[]>> CreateSessionsBulk(
        [FromBody] SleepSession[] sessions,
        CancellationToken cancellationToken = default)
    {
        if (sessions is not { Length: > 0 })
            return Problem(detail: "Sleep session data is required", statusCode: 400, title: "Bad Request");

        // Sessions embed stage intervals and biometric samples, so the cap is lower than the
        // 1000-item flat-record bulks.
        if (sessions.Length > 100)
            return Problem(detail: "Bulk operations are limited to 100 sessions per request", statusCode: 400, title: "Bad Request");

        var results = new List<SleepSession>(sessions.Length);
        try
        {
            foreach (var session in sessions)
                results.Add(await _sleepService.UpsertSessionAsync(session, cancellationToken));
        }
        catch (DbUpdateException)
        {
            // A concurrent request inserted a session with the same key between
            // the upsert's dedup lookup and its insert.
            return Problem(detail: "A sleep session with the same identifier was created concurrently.", statusCode: 409, title: "Conflict");
        }

        return StatusCode(201, results.ToArray());
    }

    /// <summary>
    /// Update an existing sleep session
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequireScope(OAuthScopes.SleepReadWrite)]
    [ProducesResponseType(typeof(SleepSession), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SleepSession>> UpdateSession(
        Guid id,
        [FromBody] SleepSession session,
        CancellationToken cancellationToken = default)
    {
        var updated = await _sleepService.UpdateSessionAsync(id, session, cancellationToken);
        if (updated == null)
            return NotFound();
        return Ok(updated);
    }

    /// <summary>
    /// Delete a sleep session
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequireScope(OAuthScopes.SleepReadWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSession(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await _sleepService.DeleteSessionAsync(id, cancellationToken);
        if (!deleted)
            return NotFound();
        return NoContent();
    }
}
