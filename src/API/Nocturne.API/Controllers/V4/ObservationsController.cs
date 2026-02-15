using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing observation data: blood glucose checks, notes, and device events
/// </summary>
[ApiController]
[Route("api/v4/observations")]
[Authorize]
[Produces("application/json")]
[Tags("V4 Observations")]
public class ObservationsController : ControllerBase
{
    private readonly BGCheckRepository _bgCheckRepo;
    private readonly NoteRepository _noteRepo;
    private readonly DeviceEventRepository _deviceEventRepo;

    public ObservationsController(
        BGCheckRepository bgCheckRepo,
        NoteRepository noteRepo,
        DeviceEventRepository deviceEventRepo)
    {
        _bgCheckRepo = bgCheckRepo;
        _noteRepo = noteRepo;
        _deviceEventRepo = deviceEventRepo;
    }

    #region BG Checks

    /// <summary>
    /// Get blood glucose checks with optional filtering
    /// </summary>
    [HttpGet("bg-checks")]
    [ProducesResponseType(typeof(PaginatedResponse<BGCheck>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<BGCheck>>> GetBGChecks(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        var descending = sort == "mills_desc";
        var data = await _bgCheckRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _bgCheckRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<BGCheck> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a blood glucose check by ID
    /// </summary>
    [HttpGet("bg-checks/{id:guid}")]
    [ProducesResponseType(typeof(BGCheck), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BGCheck>> GetBGCheckById(Guid id, CancellationToken ct = default)
    {
        var result = await _bgCheckRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new blood glucose check
    /// </summary>
    [HttpPost("bg-checks")]
    [ProducesResponseType(typeof(BGCheck), StatusCodes.Status201Created)]
    public async Task<ActionResult<BGCheck>> CreateBGCheck([FromBody] BGCheck model, CancellationToken ct = default)
    {
        var created = await _bgCheckRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetBGCheckById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing blood glucose check
    /// </summary>
    [HttpPut("bg-checks/{id:guid}")]
    [ProducesResponseType(typeof(BGCheck), StatusCodes.Status200OK)]
    public async Task<ActionResult<BGCheck>> UpdateBGCheck(Guid id, [FromBody] BGCheck model, CancellationToken ct = default)
    {
        var updated = await _bgCheckRepo.UpdateAsync(id, model, ct);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a blood glucose check
    /// </summary>
    [HttpDelete("bg-checks/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteBGCheck(Guid id, CancellationToken ct = default)
    {
        await _bgCheckRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    #endregion

    #region Notes

    /// <summary>
    /// Get notes with optional filtering
    /// </summary>
    [HttpGet("notes")]
    [ProducesResponseType(typeof(PaginatedResponse<Note>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<Note>>> GetNotes(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        var descending = sort == "mills_desc";
        var data = await _noteRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _noteRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<Note> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a note by ID
    /// </summary>
    [HttpGet("notes/{id:guid}")]
    [ProducesResponseType(typeof(Note), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Note>> GetNoteById(Guid id, CancellationToken ct = default)
    {
        var result = await _noteRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new note
    /// </summary>
    [HttpPost("notes")]
    [ProducesResponseType(typeof(Note), StatusCodes.Status201Created)]
    public async Task<ActionResult<Note>> CreateNote([FromBody] Note model, CancellationToken ct = default)
    {
        var created = await _noteRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetNoteById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing note
    /// </summary>
    [HttpPut("notes/{id:guid}")]
    [ProducesResponseType(typeof(Note), StatusCodes.Status200OK)]
    public async Task<ActionResult<Note>> UpdateNote(Guid id, [FromBody] Note model, CancellationToken ct = default)
    {
        var updated = await _noteRepo.UpdateAsync(id, model, ct);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a note
    /// </summary>
    [HttpDelete("notes/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteNote(Guid id, CancellationToken ct = default)
    {
        await _noteRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    #endregion

    #region Device Events

    /// <summary>
    /// Get device events with optional filtering
    /// </summary>
    [HttpGet("device-events")]
    [ProducesResponseType(typeof(PaginatedResponse<DeviceEvent>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<DeviceEvent>>> GetDeviceEvents(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        var descending = sort == "mills_desc";
        var data = await _deviceEventRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _deviceEventRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<DeviceEvent> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a device event by ID
    /// </summary>
    [HttpGet("device-events/{id:guid}")]
    [ProducesResponseType(typeof(DeviceEvent), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceEvent>> GetDeviceEventById(Guid id, CancellationToken ct = default)
    {
        var result = await _deviceEventRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new device event
    /// </summary>
    [HttpPost("device-events")]
    [ProducesResponseType(typeof(DeviceEvent), StatusCodes.Status201Created)]
    public async Task<ActionResult<DeviceEvent>> CreateDeviceEvent([FromBody] DeviceEvent model, CancellationToken ct = default)
    {
        var created = await _deviceEventRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetDeviceEventById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing device event
    /// </summary>
    [HttpPut("device-events/{id:guid}")]
    [ProducesResponseType(typeof(DeviceEvent), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeviceEvent>> UpdateDeviceEvent(Guid id, [FromBody] DeviceEvent model, CancellationToken ct = default)
    {
        var updated = await _deviceEventRepo.UpdateAsync(id, model, ct);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a device event
    /// </summary>
    [HttpDelete("device-events/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteDeviceEvent(Guid id, CancellationToken ct = default)
    {
        await _deviceEventRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    #endregion
}
