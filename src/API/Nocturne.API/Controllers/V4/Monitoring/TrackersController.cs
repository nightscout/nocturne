using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Services;
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
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly IAlertAcknowledgementService _acknowledgementService;
    private readonly ILogger<TrackersController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TrackersController"/>.
    /// </summary>
    /// <param name="repository">Repository for tracker definition and log persistence.</param>
    /// <param name="broadcast">Service for broadcasting real-time tracker updates via SignalR.</param>
    /// <param name="ruleSync">Synthesises managed alert rules from notification thresholds.</param>
    /// <param name="contextFactory">Tenant-scoped database context factory (managed-rule lookups).</param>
    /// <param name="acknowledgementService">Acknowledges alert excursions when a tracker is acked.</param>
    /// <param name="logger">Logger instance.</param>
    public TrackersController(
        ITrackerRepository repository,
        ISignalRBroadcastService broadcast,
        ITrackerAlertRuleSyncService ruleSync,
        ITenantDbContextFactory contextFactory,
        IAlertAcknowledgementService acknowledgementService,
        ILogger<TrackersController> logger
    )
    {
        _repository = repository;
        _broadcast = broadcast;
        _ruleSync = ruleSync;
        _contextFactory = contextFactory;
        _acknowledgementService = acknowledgementService;
        _logger = logger;
    }

    #region Helpers

    /// <summary>
    /// Check if current user can view a tracker based on visibility settings
    /// </summary>
    private bool CanViewTracker(TrackerDefinitionEntity tracker)
    {
        // Admins can see everything
        if (HttpContext.IsAdmin())
            return true;

        // Public trackers visible to everyone
        if (tracker.Visibility == TrackerVisibility.Public)
            return true;

        // The owner sees their own tracker at every visibility, not just Private, so that a
        // visibility value with no view rule of its own can never hide a tracker from the person
        // who set it. RoleRestricted is rejected on write and migrated to Private, so this is
        // belt-and-braces rather than the only thing standing between an owner and their data.
        // An unattributed tracker (UserId defaulted to "") must not match a caller carrying no
        // subject, so an empty id matches nothing.
        var currentUserId = HttpContext.GetSubjectIdString();
        if (!string.IsNullOrEmpty(currentUserId) && tracker.UserId == currentUserId)
            return true;

        // RoleRestricted has no check yet, so it falls through to hidden — including from
        // the tracker's own owner.
        return false;
    }

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
        await using var db = await _contextFactory.CreateAsync(ct);
        var tag = TrackerAlertRuleSyncService.ManagedByTag(definitionId);

        var excursions = await db.AlertExcursions
            .AsNoTracking()
            .Where(e => e.EndedAt == null && e.AcknowledgedAt == null)
            .Join(
                db.AlertRules.Where(r => r.ManagedBy == tag),
                e => e.AlertRuleId,
                r => r.Id,
                (e, r) => e.Id)
            .ToListAsync(ct);

        foreach (var excursionId in excursions)
        {
            await _acknowledgementService.AcknowledgeExcursionAsync(
                db.TenantId, excursionId, userId, broadcast: true, ct);
        }
    }

    #endregion

    #region Definitions

    /// <summary>
    /// Get all tracker definitions: the caller's own plus any Public-visibility tracker.
    /// Gated by the fallback authorization policy (no <c>[AllowAnonymous]</c>): a bare
    /// unauthenticated request on a tenant subdomain carries an empty permission trie and is
    /// rejected, so a private tenant exposes no tracker anonymously. A public-share subject is
    /// admitted by the policy but reads nothing here — tracker tables are not in
    /// <see cref="ShareDataCategories"/>, so the share RLS policy hides them.
    /// </summary>
    [HttpGet("definitions")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerDefinitionDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerDefinitionDto[]>> GetDefinitions(
        [FromQuery] TrackerCategory? category = null
    )
    {
        var userId = HttpContext.GetSubjectIdString();
        var isAuthenticated = HttpContext.IsAuthenticated();

        List<TrackerDefinitionEntity> definitions;

        if (isAuthenticated && userId != null)
        {
            // Authenticated: get user's trackers
            definitions = category.HasValue
                ? await _repository.GetDefinitionsByCategoryAsync(
                    userId,
                    category.Value,
                    HttpContext.RequestAborted
                )
                : await _repository.GetDefinitionsForUserAsync(userId, HttpContext.RequestAborted);
        }
        else
        {
            // Unauthenticated: get all definitions and filter to public only
            definitions = await _repository.GetAllDefinitionsAsync(HttpContext.RequestAborted);
        }

        // Filter by visibility
        var visible = definitions.Where(CanViewTracker).ToArray();

        return Ok(visible.Select(TrackerDefinitionDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Get a specific tracker definition
    /// </summary>
    [HttpGet("definitions/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerDefinitionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerDefinitionDto>> GetDefinition(Guid id)
    {
        var definition = await _repository.GetDefinitionByIdAsync(id, HttpContext.RequestAborted);

        if (definition == null)
            return NotFound();

        if (!CanViewTracker(definition))
            return Forbid();

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
            LowReservoirUnits = request.LowReservoirUnits,
            LowReservoirUrgency = request.LowReservoirUrgency,
            IsFavorite = request.IsFavorite,
            DashboardVisibility = request.DashboardVisibility,
            Visibility = request.Visibility,
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
                        // Alert configuration
                        PushEnabled = threshold.PushEnabled,
                        AudioEnabled = threshold.AudioEnabled,
                        AudioSound = threshold.AudioSound,
                        VibrateEnabled = threshold.VibrateEnabled,
                        RespectQuietHours = threshold.RespectQuietHours,
                    }
                );
            }
        }

        var created = await _repository.CreateDefinitionAsync(entity, HttpContext.RequestAborted);

        // CancellationToken.None: the definition is already committed, so a client
        // disconnect must not leave it without its managed rules until the next startup
        // backfill.
        await _ruleSync.SyncDefinitionAsync(created.Id, CancellationToken.None);

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

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

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

        existing.Name = request.Name ?? existing.Name;
        existing.Description = request.Description ?? existing.Description;
        existing.Category = request.Category ?? existing.Category;
        existing.Icon = request.Icon ?? existing.Icon;
        existing.TriggerEventTypes =
            request.TriggerEventTypes != null
                ? JsonSerializer.Serialize(request.TriggerEventTypes)
                : existing.TriggerEventTypes;
        existing.TriggerNotesContains =
            request.TriggerNotesContains ?? existing.TriggerNotesContains;
        existing.LifespanHours = request.LifespanHours ?? existing.LifespanHours;
        // Applied as-is (not null-means-keep): the tracker editor posts the whole
        // definition, and null must clear the level rule. Forced null for any
        // non-Reservoir category.
        existing.LowReservoirUnits =
            category == TrackerCategory.Reservoir ? request.LowReservoirUnits : null;
        existing.LowReservoirUrgency = request.LowReservoirUrgency ?? existing.LowReservoirUrgency;
        existing.IsFavorite = request.IsFavorite ?? existing.IsFavorite;
        existing.DashboardVisibility = request.DashboardVisibility ?? existing.DashboardVisibility;
        existing.Visibility = request.Visibility ?? existing.Visibility;
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
                        // Alert configuration
                        PushEnabled = t.PushEnabled,
                        AudioEnabled = t.AudioEnabled,
                        AudioSound = t.AudioSound,
                        VibrateEnabled = t.VibrateEnabled,
                        RespectQuietHours = t.RespectQuietHours,
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

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        // Definition first, rules second: if the definition delete fails the tracker
        // keeps its rules; if the rule cleanup is interrupted the leftover rules fail
        // closed (no active instance for a deleted definition) rather than a live
        // tracker losing its alerts. CancellationToken.None for the same reason.
        await _repository.DeleteDefinitionAsync(id, HttpContext.RequestAborted);
        await _ruleSync.DeleteRulesForDefinitionAsync(id, CancellationToken.None);

        _logger.LogInformation("Deleted tracker definition {Id}", id);

        return NoContent();
    }

    #endregion

    #region Instances

    /// <summary>
    /// Get active tracker instances
    /// </summary>
    [HttpGet("instances")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerInstanceDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetActiveInstances()
    {
        var userId = HttpContext.GetSubjectIdString();
        var instances = await _repository.GetActiveInstancesAsync(
            userId,
            HttpContext.RequestAborted
        );

        return Ok(instances.Select(TrackerInstanceDto.FromEntity).ToArray());
    }

    /// <summary>
    /// Get completed tracker instances (history). Matches <see cref="GetActiveInstances"/> and
    /// <see cref="GetUpcomingInstances"/>: a caller carrying no subject reads the public-visibility
    /// instances only, and the fallback authorization policy (no <c>[AllowAnonymous]</c>) rejects a
    /// bare unauthenticated request, so a private tenant exposes no history anonymously. The public
    /// share subject is still admitted by the policy, so the calendar keeps rendering history
    /// alongside the active and upcoming instances — <c>[Authorize]</c> is what 401'd it.
    /// </summary>
    [HttpGet("instances/history")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerInstanceDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetInstanceHistory(
        [FromQuery] int limit = 100
    )
    {
        limit = V4ReadLimits.ClampLimit(limit);

        var userId = HttpContext.GetSubjectIdString();
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
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerInstanceDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerInstanceDto[]>> GetUpcomingInstances(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null
    )
    {
        var userId = HttpContext.GetSubjectIdString();
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
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances"])]
    [ProducesResponseType(typeof(TrackerInstanceDto), StatusCodes.Status201Created)]
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

        // Validate mode-specific requirements
        if (definition.Mode == TrackerMode.Event && !request.ScheduledAt.HasValue)
            return Problem(detail: "Event mode trackers require a ScheduledAt datetime", statusCode: 400, title: "Bad Request");
        if (definition.Mode == TrackerMode.Duration && request.ScheduledAt.HasValue)
            return Problem(detail: "Duration mode trackers should not have a ScheduledAt datetime", statusCode: 400, title: "Bad Request");

        var instance = await _repository.StartInstanceAsync(
            request.DefinitionId,
            userId,
            request.StartNotes,
            request.StartTreatmentId,
            request.StartedAt,
            request.ScheduledAt,
            HttpContext.RequestAborted
        );

        _logger.LogInformation(
            "Started tracker instance {Id} for definition {DefinitionId}",
            instance.Id,
            request.DefinitionId
        );

        // Broadcast via SignalR
        await _broadcast.BroadcastTrackerUpdateAsync(
            "create",
            TrackerInstanceDto.FromEntity(instance)
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

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        if (existing.CompletedAt != null)
            return Problem(detail: "Instance already completed", statusCode: 400, title: "Bad Request");

        var completed = await _repository.CompleteInstanceAsync(
            id,
            request.Reason,
            request.CompletionNotes,
            request.CompleteTreatmentId,
            request.CompletedAt,
            HttpContext.RequestAborted
        );

        _logger.LogInformation(
            "Completed tracker instance {Id} with reason {Reason}",
            id,
            request.Reason
        );

        // Broadcast via SignalR
        await _broadcast.BroadcastTrackerUpdateAsync(
            "complete",
            TrackerInstanceDto.FromEntity(completed!)
        );

        return Ok(TrackerInstanceDto.FromEntity(completed!));
    }

    /// <summary>
    /// Acknowledge a tracker notification. <c>SnoozeMins</c> is stored on the instance
    /// (pill display/legacy clients); the alert-engine side is a plain acknowledgement of
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
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

        await _repository.AckInstanceAsync(id, request.SnoozeMins, HttpContext.RequestAborted);

        // Acknowledge the active excursions of this definition's managed alert rules so
        // the alert surface (history, escalation via alert_state, device intents) agrees
        // with the tracker ack instead of showing a still-unacknowledged alert.
        await AcknowledgeManagedRuleExcursionsAsync(existing.DefinitionId, userId, HttpContext.RequestAborted);

        // Broadcast ack if global
        if (request.Global)
        {
            var updated = await _repository.GetInstanceByIdAsync(id, HttpContext.RequestAborted);
            if (updated != null)
            {
                await _broadcast.BroadcastTrackerUpdateAsync(
                    "ack",
                    TrackerInstanceDto.FromEntity(updated)
                );
            }
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
    [Authorize]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerPresetDto[]), StatusCodes.Status200OK)]
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
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetPresets"])]
    [ProducesResponseType(typeof(TrackerPresetDto), StatusCodes.Status201Created)]
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

        var userId = HttpContext.GetSubjectIdString()!;
        if (existing.UserId != userId && !HttpContext.IsAdmin())
            return Forbid();

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

    // Alert configuration
    public bool PushEnabled { get; set; }
    public bool AudioEnabled { get; set; }
    public string? AudioSound { get; set; }
    public bool VibrateEnabled { get; set; }
    public bool RespectQuietHours { get; set; }

    /// <summary>
    /// The managed alert rule that delivers this threshold. Null until the sync service
    /// has run, so callers must treat it as optional.
    /// </summary>
    public Guid? AlertRuleId { get; set; }

    public static NotificationThresholdDto FromEntity(TrackerNotificationThresholdEntity entity) =>
        new()
        {
            Id = entity.Id,
            Urgency = entity.Urgency,
            Hours = entity.Hours,
            Description = entity.Description,
            DisplayOrder = entity.DisplayOrder,
            // Alert configuration
            PushEnabled = entity.PushEnabled,
            AudioEnabled = entity.AudioEnabled,
            AudioSound = entity.AudioSound,
            VibrateEnabled = entity.VibrateEnabled,
            RespectQuietHours = entity.RespectQuietHours,
            AlertRuleId = entity.AlertRuleId,
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
    /// Visibility level for this tracker: Public or Private
    /// </summary>
    public TrackerVisibility Visibility { get; set; } = TrackerVisibility.Public;

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
                JsonSerializer.Deserialize<List<string>>(entity.TriggerEventTypes) ?? [],
            TriggerNotesContains = entity.TriggerNotesContains,
            LifespanHours = entity.LifespanHours,
            LowReservoirUnits = entity.LowReservoirUnits,
            LowReservoirUrgency = entity.LowReservoirUrgency,
            // Notification thresholds
            NotificationThresholds =
                entity
                    .NotificationThresholds?.OrderBy(t => t.DisplayOrder)
                    .Select(NotificationThresholdDto.FromEntity)
                    .ToList() ?? [],
            IsFavorite = entity.IsFavorite,
            DashboardVisibility = entity.DashboardVisibility,
            Visibility = entity.Visibility,
            StartEventType = entity.StartEventType,
            CompletionEventType = entity.CompletionEventType,
            Mode = entity.Mode,
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
    public DateTime? ScheduledAt { get; set; }
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
            ScheduledAt = entity.ScheduledAt,
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
    /// Visibility level for this tracker: Public or Private. Defaults to Private so a tracker is
    /// never made Public by omission; the owner opts into Public explicitly. RoleRestricted is
    /// rejected.
    /// </summary>
    public TrackerVisibility Visibility { get; set; } = TrackerVisibility.Private;

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
    /// Visibility level for this tracker: Public or Private. Null keeps the current value, so an
    /// update never defaults a tracker to Public by omission. RoleRestricted is rejected.
    /// </summary>
    public TrackerVisibility? Visibility { get; set; }

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

    // Alert configuration (optional, defaults to disabled)
    public bool PushEnabled { get; set; }
    public bool AudioEnabled { get; set; }
    public string? AudioSound { get; set; }
    public bool VibrateEnabled { get; set; }
    public bool RespectQuietHours { get; set; } = true;
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
