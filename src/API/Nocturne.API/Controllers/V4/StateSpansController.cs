using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing time-ranged system states (pump modes, connectivity, overrides)
/// </summary>
[ApiController]
[Route("api/v4/state-spans")]
[Authorize]
public class StateSpansController : ControllerBase
{
    private readonly StateSpanRepository _repository;

    public StateSpansController(StateSpanRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Query all state spans with optional filtering
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<StateSpan>>> GetStateSpans(
        [FromQuery] StateSpanCategory? category = null,
        [FromQuery] string? state = null,
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        [FromQuery] string? source = null,
        [FromQuery] bool? active = null,
        [FromQuery] int count = 100,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var spans = await _repository.GetStateSpansAsync(
            category, state, from, to, source, active, count, skip, cancellationToken);
        return Ok(spans);
    }

    /// <summary>
    /// Get pump mode state spans
    /// </summary>
    [HttpGet("pump-modes")]
    public async Task<ActionResult<IEnumerable<StateSpan>>> GetPumpModes(
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        CancellationToken cancellationToken = default)
    {
        var spans = await _repository.GetByCategory(StateSpanCategory.PumpMode, from, to, cancellationToken);
        return Ok(spans);
    }

    /// <summary>
    /// Get connectivity state spans
    /// </summary>
    [HttpGet("connectivity")]
    public async Task<ActionResult<IEnumerable<StateSpan>>> GetConnectivity(
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        CancellationToken cancellationToken = default)
    {
        var spans = await _repository.GetByCategory(StateSpanCategory.PumpConnectivity, from, to, cancellationToken);
        return Ok(spans);
    }

    /// <summary>
    /// Get override state spans
    /// </summary>
    [HttpGet("overrides")]
    public async Task<ActionResult<IEnumerable<StateSpan>>> GetOverrides(
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        CancellationToken cancellationToken = default)
    {
        var spans = await _repository.GetByCategory(StateSpanCategory.Override, from, to, cancellationToken);
        return Ok(spans);
    }

    /// <summary>
    /// Get profile state spans
    /// </summary>
    [HttpGet("profiles")]
    public async Task<ActionResult<IEnumerable<StateSpan>>> GetProfiles(
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        CancellationToken cancellationToken = default)
    {
        var spans = await _repository.GetByCategory(StateSpanCategory.Profile, from, to, cancellationToken);
        return Ok(spans);
    }

    /// <summary>
    /// Get a specific state span by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<StateSpan>> GetStateSpan(string id, CancellationToken cancellationToken = default)
    {
        var span = await _repository.GetStateSpanByIdAsync(id, cancellationToken);
        if (span == null)
            return NotFound();
        return Ok(span);
    }

    /// <summary>
    /// Create a new state span (manual entry)
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<StateSpan>> CreateStateSpan(
        [FromBody] CreateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        var stateSpan = new StateSpan
        {
            Category = request.Category,
            State = request.State,
            StartMills = request.StartMills,
            EndMills = request.EndMills,
            Source = request.Source ?? "manual",
            Metadata = request.Metadata,
            OriginalId = request.OriginalId,
        };

        var created = await _repository.UpsertStateSpanAsync(stateSpan, cancellationToken);
        return CreatedAtAction(nameof(GetStateSpan), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing state span
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<StateSpan>> UpdateStateSpan(
        string id,
        [FromBody] UpdateStateSpanRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetStateSpanByIdAsync(id, cancellationToken);
        if (existing == null)
            return NotFound();

        var updated = new StateSpan
        {
            Id = existing.Id,
            Category = request.Category ?? existing.Category,
            State = request.State ?? existing.State,
            StartMills = request.StartMills ?? existing.StartMills,
            EndMills = request.EndMills ?? existing.EndMills,
            Source = request.Source ?? existing.Source,
            Metadata = request.Metadata ?? existing.Metadata,
            OriginalId = existing.OriginalId,
        };

        var result = await _repository.UpdateStateSpanAsync(id, updated, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Delete a state span
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteStateSpan(string id, CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteStateSpanAsync(id, cancellationToken);
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
