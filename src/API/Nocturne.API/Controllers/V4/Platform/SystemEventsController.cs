using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// Controller for managing point-in-time system events (alarms, warnings, info).
/// </summary>
/// <seealso cref="ISystemEventRepository"/>
[ApiController]
[Tags("Platform")]
[Route("api/v4/system-events")]
[Authorize]
public class SystemEventsController : ControllerBase
{
    private readonly ISystemEventRepository _repository;

    /// <summary>
    /// Initializes a new instance of <see cref="SystemEventsController"/>.
    /// </summary>
    /// <param name="repository">Repository for system event storage and retrieval.</param>
    public SystemEventsController(ISystemEventRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Query system events with optional filtering.
    /// </summary>
    /// <inheritdoc cref="ISystemEventRepository.GetSystemEventsAsync"/>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SystemEvent>>> GetSystemEvents(
        [FromQuery] SystemEventType? type = null,
        [FromQuery] SystemEventCategory? category = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? source = null,
        [FromQuery] int count = 100,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var fromMills = from.HasValue ? new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : (long?)null;
        var toMills = to.HasValue ? new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : (long?)null;
        var events = await _repository.GetSystemEventsAsync(
            type, category, fromMills, toMills, source, count, skip, cancellationToken);
        return Ok(events);
    }

    /// <summary>
    /// Get a specific system event by ID.
    /// </summary>
    /// <inheritdoc cref="ISystemEventRepository.GetSystemEventByIdAsync"/>
    [HttpGet("{id}")]
    public async Task<ActionResult<SystemEvent>> GetSystemEvent(
        string id,
        CancellationToken cancellationToken = default)
    {
        var evt = await _repository.GetSystemEventByIdAsync(id, cancellationToken);
        if (evt == null)
            return NotFound();
        return Ok(evt);
    }

    /// <summary>
    /// Create a new system event (manual entry or import).
    /// </summary>
    /// <inheritdoc cref="ISystemEventRepository.UpsertSystemEventAsync"/>
    [HttpPost]
    [ProducesResponseType(typeof(SystemEvent), StatusCodes.Status201Created)]
    public async Task<ActionResult<SystemEvent>> CreateSystemEvent(
        [FromBody] CreateSystemEventRequest request,
        CancellationToken cancellationToken = default)
    {
        var systemEvent = new SystemEvent
        {
            EventType = request.EventType,
            Category = request.Category,
            Code = request.Code,
            Description = request.Description,
            Mills = request.Mills,
            Source = request.Source ?? "manual",
            Metadata = request.Metadata,
            OriginalId = request.OriginalId,
        };

        var created = await _repository.UpsertSystemEventAsync(systemEvent, cancellationToken);
        return CreatedAtAction(nameof(GetSystemEvent), new { id = created.Id }, created);
    }

    /// <summary>
    /// Delete a system event.
    /// </summary>
    /// <inheritdoc cref="ISystemEventRepository.DeleteSystemEventAsync"/>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSystemEvent(
        string id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteSystemEventAsync(id, cancellationToken);
        if (!deleted)
            return NotFound();
        return NoContent();
    }
}

#region Request Models

/// <summary>
/// Request body for creating a new <see cref="SystemEvent"/> record.
/// </summary>
public class CreateSystemEventRequest
{
    /// <summary>Gets or sets the event type (alarm, warning, or info).</summary>
    public SystemEventType EventType { get; set; }
    /// <summary>Gets or sets the event category.</summary>
    public SystemEventCategory Category { get; set; }
    /// <summary>Gets or sets an optional short code identifying the event.</summary>
    public string? Code { get; set; }
    /// <summary>Gets or sets a human-readable description of the event.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets the Unix millisecond timestamp of the event.</summary>
    public long Mills { get; set; }
    /// <summary>Gets or sets the data source identifier (defaults to "manual" when absent).</summary>
    public string? Source { get; set; }
    /// <summary>Gets or sets arbitrary metadata associated with the event.</summary>
    public Dictionary<string, object>? Metadata { get; set; }
    /// <summary>Gets or sets the original MongoDB ObjectId, preserved for migration compatibility.</summary>
    public string? OriginalId { get; set; }
}

#endregion
