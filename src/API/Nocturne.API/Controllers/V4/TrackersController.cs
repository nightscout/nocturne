using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.API.Services;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for flexible tracker management (consumables, appointments, reminders)
/// </summary>
[ApiController]
[Authorize]
[Route("api/v4/trackers")]
[Tags("V4 Trackers")]
public class TrackersController : ControllerBase
{
    private readonly TrackerRepository _repository;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ITrackerSeedService _seedService;
    private readonly ILogger<TrackersController> _logger;

    public TrackersController(
        TrackerRepository repository,
        ISignalRBroadcastService broadcast,
        ITrackerSeedService seedService,
        ILogger<TrackersController> logger
    )
    {
        _repository = repository;
        _broadcast = broadcast;
        _seedService = seedService;
        _logger = logger;
    }

    /// <summary>
    /// Seed default tracker definitions for the current user
    /// </summary>
    [HttpPost("seed")]
    public async Task<ActionResult> SeedDefaults()
    {
        var userId = HttpContext.GetSubjectIdString()!;
        await _seedService.SeedDefaultDefinitionsAsync(userId, HttpContext.RequestAborted);
        return Ok(new { message = "Default definitions seeded successfully" });
    }

    #region Definitions

    /// <summary>
    /// Get all tracker definitions for the current user
    /// </summary>
    [HttpGet("definitions")]
    public async Task<ActionResult<TrackerDefinitionDto[]>> GetDefinitions(
        [FromQuery] TrackerCategory? category = null
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        var definitions = category.HasValue
            ? await _repository.GetDefinitionsByCategoryAsync(
                userId,
                category.Value,
                HttpContext.RequestAborted
            )
            : await _repository.GetDefinitionsForUserAsync(userId, HttpContext.RequestAborted);

        return Ok(definitions.Select(TrackerDefinitionDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Get a specific tracker definition
    /// </summary>
    [HttpGet("definitions/{id:guid}")]
    public async Task<ActionResult<TrackerDefinitionDto>> GetDefinition(Guid id)
    {
        var definition = await _repository.GetDefinitionByIdAsync(id, HttpContext.RequestAborted);

        if (definition == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        if (definition.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        return Ok(TrackerDefinitionDto.FromEntity(definition));
    }

    /// <summary>
    /// Create a new tracker definition
    /// </summary>
    [HttpPost("definitions")]
    public async Task<ActionResult<TrackerDefinitionDto>> CreateDefinition(
        [FromBody] CreateTrackerDefinitionRequest request
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        var entity = new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Icon = request.Icon ?? "activity",
            TriggerEventTypes = JsonSerializer.Serialize(request.TriggerEventTypes ?? []),
            TriggerNotesContains = request.TriggerNotesContains,
            LifespanHours = request.LifespanHours,
            InfoHours = request.InfoHours,
            WarnHours = request.WarnHours,
            HazardHours = request.HazardHours,
            UrgentHours = request.UrgentHours,
            IsFavorite = request.IsFavorite,
        };

        var created = await _repository.CreateDefinitionAsync(entity, HttpContext.RequestAborted);

        _logger.LogInformation(
            "Created tracker definition {Id} for user {UserId}",
            created.Id,
            userId
        );

        return CreatedAtAction(
            nameof(GetDefinition),
            new { id = created.Id },
            TrackerDefinitionDto.FromEntity(created)
        );
    }

    /// <summary>
    /// Update a tracker definition
    /// </summary>
    [HttpPut("definitions/{id:guid}")]
    public async Task<ActionResult<TrackerDefinitionDto>> UpdateDefinition(
        Guid id,
        [FromBody] UpdateTrackerDefinitionRequest request
    )
    {
        var existing = await _repository.GetDefinitionByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        existing.Name = request.Name ?? existing.Name;
        existing.Description = request.Description ?? existing.Description;
        existing.Category = request.Category ?? existing.Category;
        existing.Icon = request.Icon ?? existing.Icon;
        existing.TriggerEventTypes = request.TriggerEventTypes != null
            ? JsonSerializer.Serialize(request.TriggerEventTypes)
            : existing.TriggerEventTypes;
        existing.TriggerNotesContains = request.TriggerNotesContains ?? existing.TriggerNotesContains;
        existing.LifespanHours = request.LifespanHours ?? existing.LifespanHours;
        existing.InfoHours = request.InfoHours ?? existing.InfoHours;
        existing.WarnHours = request.WarnHours ?? existing.WarnHours;
        existing.HazardHours = request.HazardHours ?? existing.HazardHours;
        existing.UrgentHours = request.UrgentHours ?? existing.UrgentHours;
        existing.IsFavorite = request.IsFavorite ?? existing.IsFavorite;

        var updated = await _repository.UpdateDefinitionAsync(id, existing, HttpContext.RequestAborted);

        return Ok(TrackerDefinitionDto.FromEntity(updated!));
    }

    /// <summary>
    /// Delete a tracker definition
    /// </summary>
    [HttpDelete("definitions/{id:guid}")]
    public async Task<ActionResult> DeleteDefinition(Guid id)
    {
        var existing = await _repository.GetDefinitionByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        await _repository.DeleteDefinitionAsync(id, HttpContext.RequestAborted);

        _logger.LogInformation("Deleted tracker definition {Id}", id);

        return NoContent();
    }

    #endregion

    #region Instances

    /// <summary>
    /// Get active tracker instances
    /// </summary>
    [HttpGet("instances")]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetActiveInstances()
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var instances = await _repository.GetActiveInstancesAsync(userId, HttpContext.RequestAborted);

        return Ok(instances.Select(TrackerInstanceDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Get completed tracker instances (history)
    /// </summary>
    [HttpGet("instances/history")]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetInstanceHistory(
        [FromQuery] int limit = 100
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var instances = await _repository.GetCompletedInstancesAsync(
            userId,
            limit,
            HttpContext.RequestAborted
        );

        return Ok(instances.Select(TrackerInstanceDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Get upcoming tracker expirations for calendar
    /// </summary>
    [HttpGet("instances/upcoming")]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetUpcomingInstances(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var fromDate = from ?? DateTime.UtcNow;
        var toDate = to ?? DateTime.UtcNow.AddDays(30);

        var instances = await _repository.GetUpcomingInstancesAsync(
            userId,
            fromDate,
            toDate,
            HttpContext.RequestAborted
        );

        return Ok(instances.Select(TrackerInstanceDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Start a new tracker instance
    /// </summary>
    [HttpPost("instances")]
    public async Task<ActionResult<TrackerInstanceDto>> StartInstance(
        [FromBody] StartTrackerInstanceRequest request
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        // Verify definition exists and belongs to user
        var definition = await _repository.GetDefinitionByIdAsync(
            request.DefinitionId,
            HttpContext.RequestAborted
        );
        if (definition == null)
            return NotFound("Definition not found");

        if (definition.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        var instance = await _repository.StartInstanceAsync(
            request.DefinitionId,
            userId,
            request.StartNotes,
            request.StartTreatmentId,
            HttpContext.RequestAborted
        );

        _logger.LogInformation(
            "Started tracker instance {Id} for definition {DefinitionId}",
            instance.Id,
            request.DefinitionId
        );

        // Broadcast via SignalR
        await _broadcast.BroadcastTrackerUpdateAsync("create", TrackerInstanceDto.FromEntity(instance));

        return CreatedAtAction(
            nameof(GetActiveInstances),
            TrackerInstanceDto.FromEntity(instance)
        );
    }

    /// <summary>
    /// Complete a tracker instance
    /// </summary>
    [HttpPut("instances/{id:guid}/complete")]
    public async Task<ActionResult<TrackerInstanceDto>> CompleteInstance(
        Guid id,
        [FromBody] CompleteTrackerInstanceRequest request
    )
    {
        var existing = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        if (existing.CompletedAt != null)
            return BadRequest("Instance already completed");

        var completed = await _repository.CompleteInstanceAsync(
            id,
            request.Reason,
            request.CompletionNotes,
            request.CompleteTreatmentId,
            HttpContext.RequestAborted
        );

        _logger.LogInformation(
            "Completed tracker instance {Id} with reason {Reason}",
            id,
            request.Reason
        );

        // Broadcast via SignalR
        await _broadcast.BroadcastTrackerUpdateAsync("complete", TrackerInstanceDto.FromEntity(completed!));

        return Ok(TrackerInstanceDto.FromEntity(completed!));
    }

    /// <summary>
    /// Acknowledge/snooze a tracker notification
    /// </summary>
    [HttpPost("instances/{id:guid}/ack")]
    public async Task<ActionResult> AckInstance(Guid id, [FromBody] AckTrackerRequest request)
    {
        var existing = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        await _repository.AckInstanceAsync(id, request.SnoozeMins, HttpContext.RequestAborted);

        // Broadcast ack if global
        if (request.Global)
        {
            var updated = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
            if (updated != null)
            {
                await _broadcast.BroadcastTrackerUpdateAsync("ack", TrackerInstanceDto.FromEntity(updated));
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Delete a tracker instance
    /// </summary>
    [HttpDelete("instances/{id:guid}")]
    public async Task<ActionResult> DeleteInstance(Guid id)
    {
        var existing = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        // Broadcast before deleting to get the definition name
        var dto = TrackerInstanceDto.FromEntity(existing);
        await _repository.DeleteInstanceAsync(id, HttpContext.RequestAborted);

        // Broadcast via SignalR
        await _broadcast.BroadcastTrackerUpdateAsync("delete", dto);

        return NoContent();
    }

    #endregion

    #region Presets

    /// <summary>
    /// Get all presets for the current user
    /// </summary>
    [HttpGet("presets")]
    public async Task<ActionResult<TrackerPresetDto[]>> GetPresets()
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var presets = await _repository.GetPresetsForUserAsync(userId, HttpContext.RequestAborted);

        return Ok(presets.Select(TrackerPresetDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Create a new preset
    /// </summary>
    [HttpPost("presets")]
    public async Task<ActionResult<TrackerPresetDto>> CreatePreset(
        [FromBody] CreateTrackerPresetRequest request
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        // Verify definition exists and belongs to user
        var definition = await _repository.GetDefinitionByIdAsync(
            request.DefinitionId,
            HttpContext.RequestAborted
        );
        if (definition == null)
            return NotFound("Definition not found");

        if (definition.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        var entity = new TrackerPresetEntity
        {
            UserId = userId,
            Name = request.Name,
            DefinitionId = request.DefinitionId,
            DefaultStartNotes = request.DefaultStartNotes,
        };

        var created = await _repository.CreatePresetAsync(entity, HttpContext.RequestAborted);

        _logger.LogInformation("Created tracker preset {Id} for user {UserId}", created.Id, userId);

        return Created($"/api/v4/trackers/presets/{created.Id}", TrackerPresetDto.FromEntity(created));
    }

    /// <summary>
    /// Apply a preset (starts a new instance)
    /// </summary>
    [HttpPost("presets/{id:guid}/apply")]
    public async Task<ActionResult<TrackerInstanceDto>> ApplyPreset(
        Guid id,
        [FromBody] ApplyPresetRequest? request = null
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        var instance = await _repository.ApplyPresetAsync(
            id,
            userId,
            request?.OverrideNotes,
            HttpContext.RequestAborted
        );

        if (instance == null)
            return NotFound();

        _logger.LogInformation("Applied preset {PresetId}, created instance {InstanceId}", id, instance.Id);

        return Ok(TrackerInstanceDto.FromEntity(instance));
    }

    /// <summary>
    /// Delete a preset
    /// </summary>
    [HttpDelete("presets/{id:guid}")]
    public async Task<ActionResult> DeletePreset(Guid id)
    {
        var existing = await _repository.GetPresetByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        await _repository.DeletePresetAsync(id, HttpContext.RequestAborted);

        return NoContent();
    }

    #endregion
}

#region DTOs

public class TrackerDefinitionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TrackerCategory Category { get; set; }
    public string Icon { get; set; } = "activity";
    public List<string> TriggerEventTypes { get; set; } = [];
    public string? TriggerNotesContains { get; set; }
    public int? LifespanHours { get; set; }
    public int? InfoHours { get; set; }
    public int? WarnHours { get; set; }
    public int? HazardHours { get; set; }
    public int? UrgentHours { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public static TrackerDefinitionDto FromEntity(TrackerDefinitionEntity entity) =>
        new()
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Category = entity.Category,
            Icon = entity.Icon,
            TriggerEventTypes = JsonSerializer.Deserialize<List<string>>(entity.TriggerEventTypes) ?? [],
            TriggerNotesContains = entity.TriggerNotesContains,
            LifespanHours = entity.LifespanHours,
            InfoHours = entity.InfoHours,
            WarnHours = entity.WarnHours,
            HazardHours = entity.HazardHours,
            UrgentHours = entity.UrgentHours,
            IsFavorite = entity.IsFavorite,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
}

public class TrackerInstanceDto
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public string DefinitionName { get; set; } = string.Empty;
    public TrackerCategory Category { get; set; }
    public string Icon { get; set; } = "activity";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExpectedEndAt { get; set; }
    public string? StartNotes { get; set; }
    public string? CompletionNotes { get; set; }
    public CompletionReason? CompletionReason { get; set; }
    public double AgeHours { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastAckedAt { get; set; }
    public int? AckSnoozeMins { get; set; }

    public static TrackerInstanceDto FromEntity(TrackerInstanceEntity entity) =>
        new()
        {
            Id = entity.Id,
            DefinitionId = entity.DefinitionId,
            DefinitionName = entity.Definition?.Name ?? string.Empty,
            Category = entity.Definition?.Category ?? TrackerCategory.Custom,
            Icon = entity.Definition?.Icon ?? "activity",
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt,
            ExpectedEndAt = entity.ExpectedEndAt,
            StartNotes = entity.StartNotes,
            CompletionNotes = entity.CompletionNotes,
            CompletionReason = entity.CompletionReason,
            AgeHours = entity.AgeHours,
            IsActive = entity.IsActive,
            LastAckedAt = entity.LastAckedAt,
            AckSnoozeMins = entity.AckSnoozeMins,
        };
}

public class TrackerPresetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid DefinitionId { get; set; }
    public string DefinitionName { get; set; } = string.Empty;
    public string? DefaultStartNotes { get; set; }
    public DateTime CreatedAt { get; set; }

    public static TrackerPresetDto FromEntity(TrackerPresetEntity entity) =>
        new()
        {
            Id = entity.Id,
            Name = entity.Name,
            DefinitionId = entity.DefinitionId,
            DefinitionName = entity.Definition?.Name ?? string.Empty,
            DefaultStartNotes = entity.DefaultStartNotes,
            CreatedAt = entity.CreatedAt,
        };
}

#endregion

#region Request Models

public class CreateTrackerDefinitionRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TrackerCategory Category { get; set; } = TrackerCategory.Consumable;
    public string? Icon { get; set; }
    public List<string>? TriggerEventTypes { get; set; }
    public string? TriggerNotesContains { get; set; }
    public int? LifespanHours { get; set; }
    public int? InfoHours { get; set; }
    public int? WarnHours { get; set; }
    public int? HazardHours { get; set; }
    public int? UrgentHours { get; set; }
    public bool IsFavorite { get; set; }
}

public class UpdateTrackerDefinitionRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public TrackerCategory? Category { get; set; }
    public string? Icon { get; set; }
    public List<string>? TriggerEventTypes { get; set; }
    public string? TriggerNotesContains { get; set; }
    public int? LifespanHours { get; set; }
    public int? InfoHours { get; set; }
    public int? WarnHours { get; set; }
    public int? HazardHours { get; set; }
    public int? UrgentHours { get; set; }
    public bool? IsFavorite { get; set; }
}

public class StartTrackerInstanceRequest
{
    [Required]
    public Guid DefinitionId { get; set; }
    public string? StartNotes { get; set; }
    public string? StartTreatmentId { get; set; }
}

public class CompleteTrackerInstanceRequest
{
    [Required]
    public CompletionReason Reason { get; set; }
    public string? CompletionNotes { get; set; }
    public string? CompleteTreatmentId { get; set; }
}

public class AckTrackerRequest
{
    public int SnoozeMins { get; set; } = 30;
    public bool Global { get; set; } = false;
}

public class CreateTrackerPresetRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
    [Required]
    public Guid DefinitionId { get; set; }
    public string? DefaultStartNotes { get; set; }
}

public class ApplyPresetRequest
{
    public string? OverrideNotes { get; set; }
}

#endregion
