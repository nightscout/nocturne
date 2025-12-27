using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing point-in-time system events (alarms, warnings, info)
/// </summary>
[ApiController]
[Route("api/v4/system-events")]
[Authorize]
public class SystemEventsController : ControllerBase
{
    private readonly SystemEventRepository _repository;

    public SystemEventsController(SystemEventRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Query system events with optional filtering
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SystemEvent>>> GetSystemEvents(
        [FromQuery] SystemEventType? type = null,
        [FromQuery] SystemEventCategory? category = null,
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        [FromQuery] string? source = null,
        [FromQuery] int count = 100,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var events = await _repository.GetSystemEventsAsync(
            type, category, from, to, source, count, skip, cancellationToken);
        return Ok(events);
    }

    /// <summary>
    /// Get a specific system event by ID
    /// </summary>
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
    /// Create a new system event (manual entry or import)
    /// </summary>
    [HttpPost]
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
    /// Delete a system event
    /// </summary>
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

public class CreateSystemEventRequest
{
    public SystemEventType EventType { get; set; }
    public SystemEventCategory Category { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }
    public long Mills { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string? OriginalId { get; set; }
}

#endregion
