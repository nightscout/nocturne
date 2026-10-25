using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Controller for flexible tracker management (consumables, appointments, reminders).
/// </summary>
/// <seealso cref="ITrackerRepository"/>
/// <seealso cref="ISignalRBroadcastService"/>
[ApiController]
[Tags("Monitoring")]
[Route("api/v4/trackers")]
public class TrackersController : ControllerBase, IWriteScopedController
{
    /// <summary>
    /// The OAuth scope every write action on this controller requires. The <c>tracker_*</c> tables
    /// are not in <see cref="ShareDataCategories.GovernedTables"/> and have no V1/V3 data
    /// equivalent: a tracker is monitoring state, not a patient observation. A definition's
    /// notification thresholds are synthesised into managed alert rules
    /// (<see cref="ITrackerAlertRuleSyncService"/>), an instance is keyed to the treatment that
    /// triggered it rather than storing one, and acknowledging an instance acknowledges an alert
    /// excursion (<see cref="IAlertAcknowledgementService"/>). V1 and V2 gate their notification
    /// writes on <c>alerts.readwrite</c>. The per-action <c>[Authorize]</c> alone is satisfied by
    /// read-only credentials such as a guest-link session, which holds <c>alerts.read</c>.
    /// </summary>
    public string WriteScope => Scope.AlertsReadWrite;

    private readonly ITrackerRepository _repository;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ITrackerAlertRuleSyncService _ruleSync;
    private readonly IAlertRepository _alerts;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAlertAcknowledgementService _acknowledgementService;
    private readonly ILogger<TrackersController> _logger;
    private readonly TimeProvider _time;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Initializes a new instance of <see cref="TrackersController"/>.
    /// </summary>
    /// <param name="repository">Repository for tracker definition and log persistence.</param>
    /// <param name="broadcast">Service for broadcasting real-time tracker updates via SignalR.</param>
    /// <param name="ruleSync">Synthesises managed alert rules from notification thresholds.</param>
    /// <param name="alerts">Finds the open excursions of a definition's managed rules.</param>
    /// <param name="tenantAccessor">The request's tenant.</param>
    /// <param name="acknowledgementService">Acknowledges alert excursions when a tracker is acked.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="timeProvider">The clock a start or a range defaults from; the system clock when null.</param>
    public TrackersController(
        ITrackerRepository repository,
        ISignalRBroadcastService broadcast,
        ITrackerAlertRuleSyncService ruleSync,
        IAlertRepository alerts,
        ITenantAccessor tenantAccessor,
        IAlertAcknowledgementService acknowledgementService,
        ILogger<TrackersController> logger,
        TimeProvider? timeProvider = null
    )
    {
        _time = timeProvider ?? TimeProvider.System;
        _repository = repository;
        _broadcast = broadcast;
        _ruleSync = ruleSync;
        _alerts = alerts;
        _tenantAccessor = tenantAccessor;
        _acknowledgementService = acknowledgementService;
        _logger = logger;
    }

    #region Helpers

    /// <summary>The caller reads no trackers; see <see cref="TrackerReadAccess"/>.</summary>
    private bool ReadsNoTrackers => !TrackerReadAccess.Allows(HttpContext);

    /// <summary>
    /// A manual start replaces the running instance, as a device-event start does; see
    /// <see cref="TrackerSuccession"/>.
    /// </summary>
    private Task<TrackerSuccessionResult> StartSuccessorAsync(
        TrackerDefinitionEntity definition,
        string userId,
        string? startNotes,
        string? startTreatmentId,
        DateTime startedAt,
        DateTime? scheduledAt) =>
        TrackerSuccession.StartAsync(
            _repository,
            _broadcast,
            _logger,
            definition,
            startedAt,
            completionNotes: null,
            completeTreatmentId: null,
            (tx, ct) => tx.StartInstanceAsync(
                definition.Id,
                userId,
                startNotes,
                startTreatmentId,
                startedAt,
                scheduledAt,
                ct
            ),
            HttpContext.RequestAborted
        );

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private ObjectResult SuccessionConflict(TrackerDefinitionEntity definition, TrackerSuccessionOutcome outcome) =>
        Problem(
            detail: outcome == TrackerSuccessionOutcome.CompletedElsewhere
                ? $"The running {definition.Name} was completed while this one was starting. Try again."
                : $"A {definition.Name} started at or after this time is already running. Complete it first, or start this one later.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict"
        );

    private ObjectResult AlreadyCompleted() =>
        Problem(detail: "Instance already completed", statusCode: 400, title: "Bad Request");

    /// <summary>
    /// Validate notification thresholds for a definition
    /// </summary>
    private static string? ValidateThresholds(
        List<CreateNotificationThresholdRequest>? thresholds,
        int? lifespanHours,
        TrackerMode mode)
    {
        if (thresholds == null) return null;

        foreach (var threshold in thresholds)
        {
            // For Duration mode, negative thresholds must not exceed lifespan
            if (mode == TrackerMode.Duration && threshold.Hours < 0)
            {
                if (!lifespanHours.HasValue)
                {
                    return "Negative thresholds require a lifespan to be set";
                }
                if (Math.Abs(threshold.Hours) >= lifespanHours.Value)
                {
                    return $"Negative threshold {threshold.Hours} exceeds tracker lifespan of {lifespanHours} hours";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validate the low-reservoir level threshold for a definition
    /// </summary>
    private static string? ValidateLowReservoirUnits(double? units, TrackerCategory category)
    {
        if (units is not { } value) return null;
        if (category != TrackerCategory.Reservoir)
            return "Low reservoir units apply only to Reservoir category trackers";
        if (value <= 0)
            return "Low reservoir units must be greater than zero";
        if (value > 1000)
            return "Low reservoir units must be at most 1000";
        return null;
    }

    /// <summary>
    /// Acknowledges every open, unacknowledged excursion belonging to the managed alert
    /// rules synthesised from <paramref name="definitionId"/>'s thresholds.
    /// </summary>
    private async Task AcknowledgeManagedRuleExcursionsAsync(
        Guid definitionId, string userId, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var excursions = await _alerts.GetUnacknowledgedExcursionIdsAsync(
            tenantId, TrackerAlertRuleSyncService.ManagedByTag(definitionId), ct);

        foreach (var excursionId in excursions)
        {
            await _acknowledgementService.AcknowledgeExcursionAsync(
                tenantId, excursionId, userId, HttpContext.GetAlertAcknowledgementAuthority(),
                broadcast: true, ct);
        }
    }

    #endregion

    #region Definitions

    /// <summary>
    /// Get the tenant's tracker definitions. Gated by the fallback authorization policy (no
    /// <c>[AllowAnonymous]</c>), and empty for a caller that <see cref="ReadsNoTrackers"/>.
    /// </summary>
    [HttpGet("definitions")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerDefinitionDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerDefinitionDto[]>> GetDefinitions(
        [FromQuery] TrackerCategory? category = null
    )
    {
        if (ReadsNoTrackers)
            return Ok(Array.Empty<TrackerDefinitionDto>());

        var definitions = category.HasValue
            ? await _repository.GetDefinitionsByCategoryAsync(category.Value, HttpContext.RequestAborted)
            : await _repository.GetAllDefinitionsAsync(HttpContext.RequestAborted);

        return Ok(definitions.Select(TrackerDefinitionDto.FromEntity).ToArray());
    }

    /// <summary>
    /// The device event types a tracker can be set to restart on.
    /// </summary>
    [HttpGet("trigger-event-types")]
    [RemoteQuery]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public ActionResult<string[]> GetTriggerEventTypes() =>
        Ok(TrackerTriggerService.TriggerableEventTypes.ToArray());

    /// <summary>
    /// Get a specific tracker definition
    /// </summary>
    [HttpGet("definitions/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerDefinitionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerDefinitionDto>> GetDefinition(Guid id)
    {
        if (ReadsNoTrackers)
            return NotFound();

        var definition = await _repository.GetDefinitionByIdAsync(id, HttpContext.RequestAborted);

        if (definition == null)
            return NotFound();

        return Ok(TrackerDefinitionDto.FromEntity(definition));
    }

    /// <summary>
    /// Create a new tracker definition
    /// </summary>
    [HttpPost("definitions")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteForm(Invalidates = ["GetDefinitions"])]
    [ProducesResponseType(typeof(TrackerDefinitionDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<TrackerDefinitionDto>> CreateDefinition(
        [FromBody] CreateTrackerDefinitionRequest request
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        // Validate thresholds
        var validationError = ValidateThresholds(
            request.NotificationThresholds,
            request.LifespanHours,
            request.Mode);
        if (validationError != null)
            return BadRequest(validationError);

        // Validate mode-specific requirements
        if (request.Mode == TrackerMode.Event && request.LifespanHours.HasValue)
            return Problem(detail: "Event mode trackers should not have a lifespan", statusCode: 400, title: "Bad Request");

        var lowReservoirError = ValidateLowReservoirUnits(request.LowReservoirUnits, request.Category);
        if (lowReservoirError != null)
            return Problem(detail: lowReservoirError, statusCode: 400, title: "Bad Request");

        if (!TrackerTriggerService.TryNormaliseTriggers(request.TriggerEventTypes, out var triggers, out var triggerError))
            return Problem(detail: triggerError, statusCode: 400, title: "Bad Request");

        var entity = new TrackerDefinitionEntity
        {
            CreatedBy = userId,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Icon = request.Icon ?? "activity",
            TriggerEventTypes = JsonSerializer.Serialize(triggers ?? []),
            TriggerNotesContains = NullIfBlank(request.TriggerNotesContains),
            LifespanHours = request.LifespanHours,
            LowReservoirUnits = request.LowReservoirUnits,
            LowReservoirUrgency = request.LowReservoirUrgency,
            IsFavorite = request.IsFavorite,
            DashboardVisibility = request.DashboardVisibility,
            StartEventType = request.StartEventType,
            CompletionEventType = request.CompletionEventType,
            Mode = request.Mode,
        };

        // Add notification thresholds if provided
        if (request.NotificationThresholds != null)
        {
            foreach (var threshold in request.NotificationThresholds)
            {
                entity.NotificationThresholds.Add(
                    new TrackerNotificationThresholdEntity
                    {
                        Urgency = threshold.Urgency,
                        Hours = threshold.Hours,
                        Description = threshold.Description,
                        DisplayOrder = threshold.DisplayOrder,
                    }
                );
            }
        }

        var created = await _repository.CreateDefinitionAsync(entity, HttpContext.RequestAborted);

        // CancellationToken.None: the definition is already committed, so a client
        // disconnect must not leave it without its managed rules until the next startup
        // backfill.
        await _ruleSync.SyncDefinitionAsync(created.Id, CancellationToken.None);
        await _broadcast.BroadcastTrackerDefinitionUpdateAsync(created.Id);

        _logger.LogInformation(
            "Tracker definition {Id} created by {CreatedBy}",
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
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteForm(Invalidates = ["GetDefinitions", "GetDefinition"])]
    [ProducesResponseType(typeof(TrackerDefinitionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerDefinitionDto>> UpdateDefinition(
        Guid id,
        [FromBody] UpdateTrackerDefinitionRequest request
    )
    {
        var existing = await _repository.GetDefinitionByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        // Validate thresholds if being updated
        var mode = request.Mode ?? existing.Mode;
        var lifespan = request.LifespanHours ?? existing.LifespanHours;
        if (request.NotificationThresholds != null)
        {
            var validationError = ValidateThresholds(request.NotificationThresholds, lifespan, mode);
            if (validationError != null)
                return BadRequest(validationError);
        }

        // Validate mode-specific requirements
        if (mode == TrackerMode.Event && lifespan.HasValue)
            return Problem(detail: "Event mode trackers should not have a lifespan", statusCode: 400, title: "Bad Request");

        var category = request.Category ?? existing.Category;
        var lowReservoirError = ValidateLowReservoirUnits(request.LowReservoirUnits, category);
        if (lowReservoirError != null)
            return Problem(detail: lowReservoirError, statusCode: 400, title: "Bad Request");

        if (!TrackerTriggerService.TryNormaliseTriggers(request.TriggerEventTypes, out var triggers, out var triggerError))
            return Problem(detail: triggerError, statusCode: 400, title: "Bad Request");

        existing.Name = request.Name ?? existing.Name;
        existing.Description = request.Description ?? existing.Description;
        existing.Category = request.Category ?? existing.Category;
        existing.Icon = request.Icon ?? existing.Icon;
        // The notes filter travels with the trigger list: the editor always posts the list, but a
        // cleared filter arrives as no field at all, so null-means-keep could never clear it.
        if (triggers != null)
        {
            existing.TriggerEventTypes = JsonSerializer.Serialize(triggers);
            existing.TriggerNotesContains = NullIfBlank(request.TriggerNotesContains);
        }
        else if (request.TriggerNotesContains != null)
        {
            existing.TriggerNotesContains = NullIfBlank(request.TriggerNotesContains);
        }
        existing.LifespanHours = request.LifespanHours ?? existing.LifespanHours;
        // Applied as-is (not null-means-keep): the tracker editor posts the whole
        // definition, and null must clear the level rule. Forced null for any
        // non-Reservoir category.
        existing.LowReservoirUnits =
            category == TrackerCategory.Reservoir ? request.LowReservoirUnits : null;
        existing.LowReservoirUrgency = request.LowReservoirUrgency ?? existing.LowReservoirUrgency;
        existing.IsFavorite = request.IsFavorite ?? existing.IsFavorite;
        existing.DashboardVisibility = request.DashboardVisibility ?? existing.DashboardVisibility;
        existing.StartEventType = request.StartEventType ?? existing.StartEventType;
        existing.CompletionEventType = request.CompletionEventType ?? existing.CompletionEventType;
        existing.Mode = request.Mode ?? existing.Mode;

        // Handle notification thresholds update (replaces all existing if provided)
        if (request.NotificationThresholds != null)
        {
            await _repository.UpdateNotificationThresholdsAsync(
                id,
                request
                    .NotificationThresholds.Select(t => new TrackerNotificationThresholdEntity
                    {
                        TrackerDefinitionId = id,
                        Urgency = t.Urgency,
                        Hours = t.Hours,
                        Description = t.Description,
                        DisplayOrder = t.DisplayOrder,
                    })
                    .ToList(),
                HttpContext.RequestAborted
            );
        }

        var updated = await _repository.UpdateDefinitionAsync(
            id,
            existing,
            HttpContext.RequestAborted
        );

        // Lifespan/mode/name changes shift the synthesised conditions even when the
        // threshold list itself didn't change, so re-sync unconditionally.
        // CancellationToken.None: the threshold writes are already committed; aborting
        // here would leave stale rules firing at the old minutes.
        await _ruleSync.SyncDefinitionAsync(id, CancellationToken.None);
        await _broadcast.BroadcastTrackerDefinitionUpdateAsync(id);

        return Ok(TrackerDefinitionDto.FromEntity(updated!));
    }

    /// <summary>
    /// Delete a tracker definition
    /// </summary>
    [HttpDelete("definitions/{id:guid}")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetDefinitions"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteDefinition(Guid id)
    {
        var existing = await _repository.GetDefinitionByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        // Definition first, rules second: if the definition delete fails the tracker
        // keeps its rules; if the rule cleanup is interrupted the leftover rules fail
        // closed (no active instance for a deleted definition) rather than a live
        // tracker losing its alerts. CancellationToken.None for the same reason.
        await _repository.DeleteDefinitionAsync(id, HttpContext.RequestAborted);
        await _ruleSync.DeleteRulesForDefinitionAsync(id, CancellationToken.None);
        await _broadcast.BroadcastTrackerDefinitionUpdateAsync(id);

        _logger.LogInformation("Deleted tracker definition {Id}", id);

        return NoContent();
    }

    #endregion

    #region Instances

    /// <summary>
    /// Get the tenant's active tracker instances, empty for a caller that
    /// <see cref="ReadsNoTrackers"/>.
    /// </summary>
    [HttpGet("instances")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerInstanceDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetActiveInstances()
    {
        if (ReadsNoTrackers)
            return Ok(Array.Empty<TrackerInstanceDto>());

        var instances = await _repository.GetActiveInstancesAsync(HttpContext.RequestAborted);

        return Ok(instances.Select(TrackerInstanceDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Get completed tracker instances (history), empty for a caller that
    /// <see cref="ReadsNoTrackers"/>. Gated by the fallback policy rather than <c>[Authorize]</c>,
    /// as <see cref="GetActiveInstances"/> and <see cref="GetUpcomingInstances"/> are, so a share's
    /// calendar reads an empty list rather than a 401.
    /// </summary>
    [HttpGet("instances/history")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerInstanceDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetInstanceHistory(
        [FromQuery] int limit = 100
    )
    {
        limit = V4ReadLimits.ClampLimit(limit);

        if (ReadsNoTrackers)
            return Ok(Array.Empty<TrackerInstanceDto>());

        var instances = await _repository.GetCompletedInstancesAsync(limit, HttpContext.RequestAborted);

        return Ok(instances.Select(TrackerInstanceDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Get upcoming tracker expirations for calendar, empty for a caller that
    /// <see cref="ReadsNoTrackers"/>.
    /// </summary>
    [HttpGet("instances/upcoming")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerInstanceDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetUpcomingInstances(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null
    )
    {
        if (ReadsNoTrackers)
            return Ok(Array.Empty<TrackerInstanceDto>());

        var fromDate = from ?? UtcNow;
        var toDate = to ?? UtcNow.AddDays(30);

        var instances = await _repository.GetUpcomingInstancesAsync(
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
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances"])]
    [ProducesResponseType(typeof(TrackerInstanceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TrackerInstanceDto>> StartInstance(
        [FromBody] StartTrackerInstanceRequest request
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        var definition = await _repository.GetDefinitionByIdAsync(
            request.DefinitionId,
            HttpContext.RequestAborted
        );
        if (definition == null)
            return NotFound("Definition not found");

        // Validate mode-specific requirements
        if (definition.Mode == TrackerMode.Event && !request.ScheduledAt.HasValue)
            return Problem(detail: "Event mode trackers require a ScheduledAt datetime", statusCode: 400, title: "Bad Request");
        if (definition.Mode == TrackerMode.Duration && request.ScheduledAt.HasValue)
            return Problem(detail: "Duration mode trackers should not have a ScheduledAt datetime", statusCode: 400, title: "Bad Request");

        var succession = await StartSuccessorAsync(
            definition,
            userId,
            request.StartNotes,
            request.StartTreatmentId,
            request.StartedAt ?? UtcNow,
            request.ScheduledAt
        );
        if (succession.Started is not { } instance)
            return SuccessionConflict(definition, succession.Outcome);

        _logger.LogInformation(
            "Started tracker instance {Id} for definition {DefinitionId}",
            instance.Id,
            request.DefinitionId
        );

        return CreatedAtAction(nameof(GetActiveInstances), TrackerInstanceDto.FromEntity(instance));
    }

    /// <summary>
    /// Complete a tracker instance
    /// </summary>
    [HttpPut("instances/{id:guid}/complete")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances", "GetInstanceHistory"])]
    [ProducesResponseType(typeof(TrackerInstanceDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerInstanceDto>> CompleteInstance(
        Guid id,
        [FromBody] CompleteTrackerInstanceRequest request
    )
    {
        var existing = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        if (existing.CompletedAt != null)
            return AlreadyCompleted();

        var completed = await _repository.CompleteInstanceAsync(
            id,
            request.Reason,
            request.CompletionNotes,
            request.CompleteTreatmentId,
            request.CompletedAt,
            HttpContext.RequestAborted
        );
        if (completed is null)
            return AlreadyCompleted();

        _logger.LogInformation(
            "Completed tracker instance {Id} with reason {Reason}",
            id,
            request.Reason
        );

        // Broadcast via SignalR
        await _broadcast.BroadcastTrackerUpdateAsync(
            TrackerRealtimeAction.Complete,
            TrackerInstanceDto.FromEntity(completed)
        );

        return Ok(TrackerInstanceDto.FromEntity(completed));
    }

    /// <summary>
    /// Acknowledge a tracker notification. <c>SnoozeMins</c> is stored on the instance and
    /// broadcast to the tenant's clients; the alert-engine side is a plain acknowledgement of
    /// the managed rules' open excursions — re-notification is the threshold ladder's and
    /// alert_state escalation rules' job, not a snooze re-fire.
    /// </summary>
    [HttpPost("instances/{id:guid}/ack")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> AckInstance(Guid id, [FromBody] AckTrackerRequest request)
    {
        var existing = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        var userId = HttpContext.GetSubjectIdString()!;
        await _repository.AckInstanceAsync(id, request.SnoozeMins, HttpContext.RequestAborted);

        // Acknowledge the active excursions of this definition's managed alert rules so
        // the alert surface (history, escalation via alert_state, device intents) agrees
        // with the tracker ack instead of showing a still-unacknowledged alert.
        await AcknowledgeManagedRuleExcursionsAsync(existing.DefinitionId, userId, HttpContext.RequestAborted);

        var updated = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
        if (updated != null)
        {
            await _broadcast.BroadcastTrackerUpdateAsync(
                TrackerRealtimeAction.Ack,
                TrackerInstanceDto.FromEntity(updated)
            );
        }

        return NoContent();
    }

    /// <summary>
    /// Delete a tracker instance
    /// </summary>
    [HttpDelete("instances/{id:guid}")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteInstance(Guid id)
    {
        var existing = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        // Broadcast before deleting to get the definition name
        var dto = TrackerInstanceDto.FromEntity(existing);
        await _repository.DeleteInstanceAsync(id, HttpContext.RequestAborted);

        // Broadcast via SignalR
        await _broadcast.BroadcastTrackerUpdateAsync(TrackerRealtimeAction.Delete, dto);

        return NoContent();
    }

    #endregion

    #region Presets

    /// <summary>
    /// Get the tenant's presets, empty for a caller that <see cref="ReadsNoTrackers"/>.
    /// </summary>
    [HttpGet("presets")]
    [Authorize]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerPresetDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerPresetDto[]>> GetPresets()
    {
        if (ReadsNoTrackers)
            return Ok(Array.Empty<TrackerPresetDto>());

        var presets = await _repository.GetPresetsAsync(HttpContext.RequestAborted);

        return Ok(presets.Select(TrackerPresetDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Create a new preset
    /// </summary>
    [HttpPost("presets")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetPresets"])]
    [ProducesResponseType(typeof(TrackerPresetDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<TrackerPresetDto>> CreatePreset(
        [FromBody] CreateTrackerPresetRequest request
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        var definition = await _repository.GetDefinitionByIdAsync(
            request.DefinitionId,
            HttpContext.RequestAborted
        );
        if (definition == null)
            return NotFound("Definition not found");

        var entity = new TrackerPresetEntity
        {
            CreatedBy = userId,
            Name = request.Name,
            DefinitionId = request.DefinitionId,
            DefaultStartNotes = request.DefaultStartNotes,
        };

        var created = await _repository.CreatePresetAsync(entity, HttpContext.RequestAborted);

        _logger.LogInformation("Tracker preset {Id} created by {CreatedBy}", created.Id, userId);

        return Created(
            $"/api/v4/trackers/presets/{created.Id}",
            TrackerPresetDto.FromEntity(created)
        );
    }

    /// <summary>
    /// Apply a preset (starts a new instance)
    /// </summary>
    [HttpPost("presets/{id:guid}/apply")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances"])]
    [ProducesResponseType(typeof(TrackerInstanceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TrackerInstanceDto>> ApplyPreset(
        Guid id,
        [FromBody] ApplyPresetRequest? request = null
    )
    {
        var userId = HttpContext.GetSubjectIdString()!;

        var preset = await _repository.GetPresetByIdAsync(id, HttpContext.RequestAborted);
        if (preset == null)
            return NotFound();

        var succession = await StartSuccessorAsync(
            preset.Definition,
            userId,
            request?.OverrideNotes ?? preset.DefaultStartNotes,
            startTreatmentId: null,
            UtcNow,
            scheduledAt: null
        );
        if (succession.Started is not { } instance)
            return SuccessionConflict(preset.Definition, succession.Outcome);

        _logger.LogInformation(
            "Applied preset {PresetId}, created instance {InstanceId}",
            id,
            instance.Id
        );

        return Ok(TrackerInstanceDto.FromEntity(instance));
    }

    /// <summary>
    /// Delete a preset
    /// </summary>
    [HttpDelete("presets/{id:guid}")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetPresets"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeletePreset(Guid id)
    {
        var existing = await _repository.GetPresetByIdAsync(id, HttpContext.RequestAborted);
        if (existing == null)
            return NotFound();

        await _repository.DeletePresetAsync(id, HttpContext.RequestAborted);

        return NoContent();
    }

    #endregion
}

#region DTOs

public class NotificationThresholdDto
{
    public Guid? Id { get; set; }
    public NotificationUrgency Urgency { get; set; }
    public int Hours { get; set; }
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }

    /// <summary>
    /// The managed alert rule that delivers this threshold. Null until the sync service
    /// has run, so callers must treat it as optional.
    /// </summary>
    public Guid? AlertRuleId { get; set; }

    /// <summary>
    /// Minutes from a run's reference time (start, or the scheduled time for an Event tracker) at
    /// which this threshold fires, resolved by <see cref="TrackerSchedule.OffsetMinutes"/>. Null when
    /// it can never fire. <see cref="Hours"/> is what the user typed; this is what it means.
    /// </summary>
    public int? OffsetMinutes { get; set; }

    public static NotificationThresholdDto FromEntity(
        TrackerNotificationThresholdEntity entity, TrackerDefinitionEntity definition) =>
        new()
        {
            Id = entity.Id,
            Urgency = entity.Urgency,
            Hours = entity.Hours,
            Description = entity.Description,
            DisplayOrder = entity.DisplayOrder,
            AlertRuleId = entity.AlertRuleId,
            OffsetMinutes = TrackerSchedule.OffsetMinutes(definition.Mode, definition.LifespanHours, entity.Hours),
        };
}

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

    /// <summary>
    /// Reservoir category only: units level below which the synced managed reservoir
    /// rule fires. Null = no level rule.
    /// </summary>
    public double? LowReservoirUnits { get; set; }

    /// <summary>
    /// Urgency of the low-reservoir level rule.
    /// </summary>
    public NotificationUrgency LowReservoirUrgency { get; set; } = NotificationUrgency.Warn;

    // Notification thresholds (many-to-one relationship)
    public List<NotificationThresholdDto> NotificationThresholds { get; set; } = [];

    public bool IsFavorite { get; set; }

    /// <summary>
    /// Dashboard visibility: Off, Always, Info, Warn, Hazard, Urgent
    /// </summary>
    public DashboardVisibility DashboardVisibility { get; set; } = DashboardVisibility.Always;

    /// <summary>
    /// Event type to create when tracker is started (for Nightscout compatibility)
    /// </summary>
    public string? StartEventType { get; set; }

    /// <summary>
    /// Event type to create when tracker is completed (for Nightscout compatibility)
    /// </summary>
    public string? CompletionEventType { get; set; }

    /// <summary>
    /// Tracker mode: Duration or Event
    /// </summary>
    public TrackerMode Mode { get; set; } = TrackerMode.Duration;

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
            TriggerEventTypes =
            [
                .. (JsonSerializer.Deserialize<List<string>>(entity.TriggerEventTypes) ?? [])
                    .Select(TrackerTriggerService.CanonicalTrigger)
                    .OfType<string>(),
            ],
            TriggerNotesContains = entity.TriggerNotesContains,
            LifespanHours = entity.LifespanHours,
            LowReservoirUnits = entity.LowReservoirUnits,
            LowReservoirUrgency = entity.LowReservoirUrgency,
            // Notification thresholds
            NotificationThresholds =
                entity
                    .NotificationThresholds?.OrderBy(t => t.DisplayOrder)
                    .Select(t => NotificationThresholdDto.FromEntity(t, entity))
                    .ToList() ?? [],
            IsFavorite = entity.IsFavorite,
            DashboardVisibility = entity.DashboardVisibility,
            StartEventType = entity.StartEventType,
            CompletionEventType = entity.CompletionEventType,
            Mode = entity.Mode,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
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

    /// <summary>
    /// Reservoir category only: units level below which the synced managed reservoir
    /// rule fires. Null = no level rule.
    /// </summary>
    public double? LowReservoirUnits { get; set; }

    /// <summary>
    /// Urgency of the low-reservoir level rule.
    /// </summary>
    public NotificationUrgency LowReservoirUrgency { get; set; } = NotificationUrgency.Warn;

    // Notification thresholds (many-to-one relationship)
    public List<CreateNotificationThresholdRequest>? NotificationThresholds { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Dashboard visibility: Off, Always, Info, Warn, Hazard, Urgent
    /// </summary>
    public DashboardVisibility DashboardVisibility { get; set; } = DashboardVisibility.Always;

    /// <summary>
    /// Event type to create when tracker is started (for Nightscout compatibility)
    /// </summary>
    public string? StartEventType { get; set; }

    /// <summary>
    /// Event type to create when tracker is completed (for Nightscout compatibility)
    /// </summary>
    public string? CompletionEventType { get; set; }

    /// <summary>
    /// Tracker mode: Duration (time-based) or Event (scheduled datetime)
    /// </summary>
    public TrackerMode Mode { get; set; } = TrackerMode.Duration;
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

    /// <summary>
    /// Reservoir category only: units level below which the synced managed reservoir
    /// rule fires. The tracker editor posts the whole definition, so for a Reservoir
    /// definition this value is applied as-is on every update — null clears the rule
    /// (null-means-keep cannot express clearing).
    /// </summary>
    public double? LowReservoirUnits { get; set; }

    /// <summary>
    /// Urgency of the low-reservoir level rule. Null keeps the current value.
    /// </summary>
    public NotificationUrgency? LowReservoirUrgency { get; set; }

    // Notification thresholds (if provided, replaces all existing thresholds)
    public List<CreateNotificationThresholdRequest>? NotificationThresholds { get; set; }
    public bool? IsFavorite { get; set; }

    /// <summary>
    /// Dashboard visibility: Off, Always, Info, Warn, Hazard, Urgent
    /// </summary>
    public DashboardVisibility? DashboardVisibility { get; set; }

    /// <summary>
    /// Event type to create when tracker is started (for Nightscout compatibility)
    /// </summary>
    public string? StartEventType { get; set; }

    /// <summary>
    /// Event type to create when tracker is completed (for Nightscout compatibility)
    /// </summary>
    public string? CompletionEventType { get; set; }

    /// <summary>
    /// Tracker mode: Duration (time-based) or Event (scheduled datetime)
    /// </summary>
    public TrackerMode? Mode { get; set; }
}

public class CreateNotificationThresholdRequest
{
    public NotificationUrgency Urgency { get; set; }
    public int Hours { get; set; }
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
}

public class StartTrackerInstanceRequest
{
    [Required]
    public Guid DefinitionId { get; set; }
    public string? StartNotes { get; set; }
    public string? StartTreatmentId { get; set; }

    /// <summary>
    /// Optional custom start time for backdating. Defaults to now if not provided.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// For Event mode: the scheduled datetime of the event
    /// </summary>
    public DateTime? ScheduledAt { get; set; }
}

public class CompleteTrackerInstanceRequest
{
    [Required]
    public CompletionReason Reason { get; set; }
    public string? CompletionNotes { get; set; }
    public string? CompleteTreatmentId { get; set; }

    /// <summary>
    /// Optional custom completion time for backdating. Defaults to now if not provided.
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}

public class AckTrackerRequest
{
    public int SnoozeMins { get; set; } = 30;
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
