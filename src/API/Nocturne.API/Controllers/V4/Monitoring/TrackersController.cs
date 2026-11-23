using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Trackers for consumables, appointments and reminders. Manage tracker definitions and presets,
/// and start, complete, acknowledge and delete tracker instances.
/// </summary>
/// <seealso cref="ITrackerService"/>
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

    private readonly ITrackerService _trackers;
    private readonly TimeProvider _time;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Initializes a new instance of <see cref="TrackersController"/>.
    /// </summary>
    /// <param name="trackers">The tracker module every read and write goes through.</param>
    /// <param name="timeProvider">The clock a range and the templates default from; the system clock when null.</param>
    public TrackersController(ITrackerService trackers, TimeProvider? timeProvider = null)
    {
        _trackers = trackers;
        _time = timeProvider ?? TimeProvider.System;
    }

    #region Helpers

    /// <summary>The caller reads no trackers; see <see cref="TrackerReadAccess"/>.</summary>
    private bool ReadsNoTrackers => !TrackerReadAccess.Allows(HttpContext);

    private ObjectResult BadRequestProblem(string detail) =>
        Problem(detail: detail, statusCode: 400, title: "Bad Request");

    /// <summary>
    /// A threshold that breaks a rule answers a plain 400 with the message as its body; every other
    /// rule answers a problem.
    /// </summary>
    private ActionResult DefinitionError(TrackerDefinitionError error) =>
        error.Rule == TrackerDefinitionRule.Thresholds
            ? BadRequest(error.Message)
            : BadRequestProblem(error.Message);

    private ObjectResult SuccessionConflict(TrackerDefinition definition, TrackerSuccessionOutcome outcome) =>
        Problem(
            detail: outcome == TrackerSuccessionOutcome.CompletedElsewhere
                ? $"The running {definition.Name} was completed while this one was starting. Try again."
                : $"A {definition.Name} started at or after this time is already running. Complete it first, or start this one later.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict"
        );

    private static TrackerNotificationThreshold Threshold(CreateNotificationThresholdRequest request) =>
        new(Guid.Empty, request.Urgency, request.Hours, request.Description, request.DisplayOrder);

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

        var definitions = await _trackers.GetDefinitionsAsync(category, HttpContext.RequestAborted);

        return Ok(definitions.Select(TrackerDefinitionDto.From).ToArray());
    }

    /// <summary>
    /// The ready-made trackers the tracker wizard offers, each an unsaved definition for the
    /// tenant's registered devices (<see cref="TrackerTemplates"/>). Empty for a caller that
    /// <see cref="ReadsNoTrackers"/>. A caller without <see cref="Scope.DevicesRead"/> gets the
    /// templates of a tenant with no devices, since a template names the device it is for.
    /// </summary>
    [HttpGet("templates")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TrackerTemplateDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerTemplateDto[]>> GetTemplates(
        [FromServices] IPatientDeviceRepository patientDevices)
    {
        if (ReadsNoTrackers)
            return Ok(Array.Empty<TrackerTemplateDto>());

        IReadOnlyCollection<PatientDevice> devices = HttpContext.HasScope(Scope.DevicesRead)
            ? [.. await patientDevices.GetCurrentAsync(HttpContext.RequestAborted)]
            : [];
        var now = UtcNow;
        return Ok(TrackerTemplates.For(devices)
            .Select(t => new TrackerTemplateDto
            {
                Id = t.Id,
                Definition = TrackerDefinitionDto.From(t.Draft, now),
            })
            .ToArray());
    }

    /// <summary>
    /// The device event types a tracker can be set to restart on.
    /// </summary>
    [HttpGet("trigger-event-types")]
    [RemoteQuery]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public ActionResult<string[]> GetTriggerEventTypes() =>
        Ok(TrackerEventTypes.TriggerableEventTypes.ToArray());

    /// <summary>
    /// The device event types a tracker can be set to end on, completing its run without starting
    /// another.
    /// </summary>
    [HttpGet("end-event-types")]
    [RemoteQuery]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public ActionResult<string[]> GetEndEventTypes() =>
        Ok(TrackerEventTypes.EndingEventTypes.ToArray());

    /// <summary>
    /// The device event types a definition can create when a run starts or completes: the Nightscout
    /// names that map to a device event.
    /// </summary>
    [HttpGet("device-event-types")]
    [RemoteQuery]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public ActionResult<string[]> GetDeviceEventTypes() =>
        Ok(TrackerEventTypes.DeviceEventTypes.ToArray());

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

        var definition = await _trackers.GetDefinitionAsync(id, HttpContext.RequestAborted);

        if (definition == null)
            return NotFound();

        return Ok(TrackerDefinitionDto.From(definition));
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
        var result = await _trackers.CreateDefinitionAsync(
            new TrackerDefinitionDraft
            {
                Name = request.Name,
                Description = request.Description,
                Category = request.Category,
                Icon = request.Icon,
                TriggerEventTypes = request.TriggerEventTypes,
                EndEventTypes = request.EndEventTypes,
                Covers = request.Covers,
                TriggerNotesContains = request.TriggerNotesContains,
                LifespanHours = request.LifespanHours,
                LowReservoirUnits = request.LowReservoirUnits,
                LowReservoirUrgency = request.LowReservoirUrgency,
                NotificationThresholds = request.NotificationThresholds?.Select(Threshold).ToList(),
                IsFavorite = request.IsFavorite,
                DashboardVisibility = request.DashboardVisibility,
                StartEventType = request.StartEventType,
                CompletionEventType = request.CompletionEventType,
                Mode = request.Mode,
            },
            HttpContext.GetSubjectIdString()!,
            HttpContext.RequestAborted);
        if (result.Definition is not { } created)
            return DefinitionError(result.Error!);

        return CreatedAtAction(
            nameof(GetDefinition),
            new { id = created.Id },
            TrackerDefinitionDto.From(created)
        );
    }

    /// <summary>
    /// Update a tracker definition
    /// </summary>
    [HttpPut("definitions/{id:guid}")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteForm(Invalidates = ["GetDefinitions", "GetDefinition", "GetActiveInstances", "GetInstanceHistory", "GetPresets"])]
    [ProducesResponseType(typeof(TrackerDefinitionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrackerDefinitionDto>> UpdateDefinition(
        Guid id,
        [FromBody] UpdateTrackerDefinitionRequest request
    )
    {
        var result = await _trackers.UpdateDefinitionAsync(
            id,
            new TrackerDefinitionEdit
            {
                Name = request.Name,
                Description = request.Description,
                Category = request.Category,
                Icon = request.Icon,
                TriggerEventTypes = request.TriggerEventTypes,
                EndEventTypes = request.EndEventTypes,
                Covers = request.Covers,
                TriggerNotesContains = request.TriggerNotesContains,
                LifespanHours = request.LifespanHours,
                LowReservoirUnits = request.LowReservoirUnits,
                LowReservoirUrgency = request.LowReservoirUrgency,
                NotificationThresholds = request.NotificationThresholds?.Select(Threshold).ToList(),
                IsFavorite = request.IsFavorite,
                DashboardVisibility = request.DashboardVisibility,
                StartEventType = request.StartEventType,
                CompletionEventType = request.CompletionEventType,
                Mode = request.Mode,
            },
            HttpContext.RequestAborted);
        if (result is null)
            return NotFound();
        if (result.Definition is not { } updated)
            return DefinitionError(result.Error!);

        return Ok(TrackerDefinitionDto.From(updated));
    }

    /// <summary>
    /// Delete a tracker definition
    /// </summary>
    [HttpDelete("definitions/{id:guid}")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetDefinitions", "GetActiveInstances", "GetInstanceHistory", "GetPresets"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteDefinition(Guid id) =>
        await _trackers.DeleteDefinitionAsync(id, HttpContext.RequestAborted) ? NoContent() : NotFound();

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

        var instances = await _trackers.GetActiveInstancesAsync(HttpContext.RequestAborted);

        return Ok(instances.Select(i => TrackerInstanceDto.From(i, UtcNow)).ToArray());
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

        var instances = await _trackers.GetInstanceHistoryAsync(limit, HttpContext.RequestAborted);

        return Ok(instances.Select(i => TrackerInstanceDto.From(i, UtcNow)).ToArray());
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

        var instances = await _trackers.GetUpcomingInstancesAsync(
            from ?? UtcNow,
            to ?? UtcNow.AddDays(30),
            HttpContext.RequestAborted
        );

        return Ok(instances.Select(i => TrackerInstanceDto.From(i, UtcNow)).ToArray());
    }

    /// <summary>
    /// Start a new tracker instance
    /// </summary>
    [HttpPost("instances")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances", "GetInstanceHistory"])]
    [ProducesResponseType(typeof(TrackerInstanceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TrackerInstanceDto>> StartInstance(
        [FromBody] StartTrackerInstanceRequest request
    )
    {
        var result = await _trackers.StartInstanceAsync(
            request.DefinitionId,
            HttpContext.GetSubjectIdString()!,
            request.StartNotes,
            request.StartTreatmentId,
            request.StartedAt,
            request.ScheduledAt,
            HttpContext.RequestAborted
        );
        if (result.Definition is not { } definition)
            return NotFound("Definition not found");
        if (result.Invalid is { } invalid)
            return BadRequestProblem(invalid);
        if (result.Started is not { } instance)
            return SuccessionConflict(definition, result.Succession!.Outcome);

        return CreatedAtAction(nameof(GetActiveInstances), TrackerInstanceDto.From(instance, UtcNow));
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
        var completion = await _trackers.CompleteInstanceAsync(
            id,
            request.Reason,
            request.CompletionNotes,
            request.CompleteTreatmentId,
            request.CompletedAt,
            HttpContext.RequestAborted
        );

        return completion.Outcome switch
        {
            TrackerCompletionOutcome.NotFound => NotFound(),
            TrackerCompletionOutcome.AlreadyCompleted =>
                BadRequestProblem("Instance already completed"),
            _ => Ok(TrackerInstanceDto.From(completion.Completed!, UtcNow)),
        };
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
    public async Task<ActionResult> AckInstance(Guid id, [FromBody] AckTrackerRequest request) =>
        await _trackers.AcknowledgeInstanceAsync(
            id,
            request.SnoozeMins,
            HttpContext.GetSubjectIdString()!,
            HttpContext.GetAlertAcknowledgementAuthority(),
            HttpContext.RequestAborted)
            ? NoContent()
            : NotFound();

    /// <summary>
    /// Delete a tracker instance
    /// </summary>
    [HttpDelete("instances/{id:guid}")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteInstance(Guid id) =>
        await _trackers.DeleteInstanceAsync(id, HttpContext.RequestAborted) ? NoContent() : NotFound();

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

        var presets = await _trackers.GetPresetsAsync(HttpContext.RequestAborted);

        return Ok(presets.Select(TrackerPresetDto.From).ToArray());
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
        var created = await _trackers.CreatePresetAsync(
            request.Name,
            request.DefinitionId,
            request.DefaultStartNotes,
            HttpContext.GetSubjectIdString()!,
            HttpContext.RequestAborted
        );
        if (created == null)
            return NotFound("Definition not found");

        return Created(
            $"/api/v4/trackers/presets/{created.Id}",
            TrackerPresetDto.From(created)
        );
    }

    /// <summary>
    /// Apply a preset (starts a new instance)
    /// </summary>
    [HttpPost("presets/{id:guid}/apply")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetActiveInstances", "GetInstanceHistory"])]
    [ProducesResponseType(typeof(TrackerInstanceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TrackerInstanceDto>> ApplyPreset(
        Guid id,
        [FromBody] ApplyPresetRequest? request = null
    )
    {
        var result = await _trackers.ApplyPresetAsync(
            id,
            HttpContext.GetSubjectIdString()!,
            request?.OverrideNotes,
            HttpContext.RequestAborted
        );
        if (result?.Definition is not { } definition)
            return NotFound();
        if (result.Started is not { } instance)
            return SuccessionConflict(definition, result.Succession!.Outcome);

        return Ok(TrackerInstanceDto.From(instance, UtcNow));
    }

    /// <summary>
    /// Delete a preset
    /// </summary>
    [HttpDelete("presets/{id:guid}")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetPresets"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeletePreset(Guid id) =>
        await _trackers.DeletePresetAsync(id, HttpContext.RequestAborted) ? NoContent() : NotFound();

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

    public static NotificationThresholdDto From(
        TrackerNotificationThreshold threshold, TrackerDefinition definition) =>
        From(threshold, definition.Mode, definition.LifespanHours);

    public static NotificationThresholdDto From(
        TrackerNotificationThreshold threshold, TrackerMode mode, int? lifespanHours) =>
        new()
        {
            Id = threshold.Id,
            Urgency = threshold.Urgency,
            Hours = threshold.Hours,
            Description = threshold.Description,
            DisplayOrder = threshold.DisplayOrder,
            AlertRuleId = threshold.AlertRuleId,
            OffsetMinutes = TrackerSchedule.OffsetMinutes(mode, lifespanHours, threshold.Hours),
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

    /// <summary>
    /// Device event types that complete the running instance without starting another.
    /// </summary>
    public List<string> EndEventTypes { get; set; } = [];

    /// <summary>
    /// What the tracked hardware provides (<see cref="TrackerCoverage.Of(TrackerDefinition)"/>).
    /// Empty when not known, and then what the tracker stands for is read from its triggers and
    /// category.
    /// </summary>
    public List<PumpCapability> Covers { get; set; } = [];

    /// <summary>
    /// The catalog pump part the tracker follows, as the catalog id and the part's key
    /// (e.g. "omnipod-5/pod"). Null for a tracker that follows no catalog part.
    /// </summary>
    public string? DeviceComponent { get; set; }
    public string? TriggerNotesContains { get; set; }
    public int? LifespanHours { get; set; }

    /// <summary>
    /// For a tracker that holds insulin (a Reservoir category, or one covering Reservoir): units
    /// level below which the synced managed reservoir rule fires. Null = no level rule.
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

    public static TrackerDefinitionDto From(TrackerDefinition definition) =>
        new()
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            Category = definition.Category,
            Icon = definition.Icon,
            TriggerEventTypes =
                [.. definition.TriggerEventTypes.Select(TrackerEventTypes.CanonicalTrigger).OfType<string>()],
            EndEventTypes =
                [.. definition.EndEventTypes.Select(TrackerEventTypes.CanonicalEnd).OfType<string>()],
            Covers = [.. TrackerCoverage.Of(definition).Order()],
            DeviceComponent = definition.DeviceComponent,
            TriggerNotesContains = definition.TriggerNotesContains,
            LifespanHours = definition.LifespanHours,
            LowReservoirUnits = definition.LowReservoirUnits,
            LowReservoirUrgency = definition.LowReservoirUrgency,
            NotificationThresholds =
            [
                .. definition.NotificationThresholds
                    .OrderBy(t => t.DisplayOrder)
                    .Select(t => NotificationThresholdDto.From(t, definition)),
            ],
            IsFavorite = definition.IsFavorite,
            DashboardVisibility = definition.DashboardVisibility,
            StartEventType = definition.StartEventType,
            CompletionEventType = definition.CompletionEventType,
            Mode = definition.Mode,
            CreatedAt = definition.CreatedAt,
            UpdatedAt = definition.UpdatedAt,
        };

    /// <summary>
    /// An unsaved <paramref name="draft"/> served as a definition made at <paramref name="createdAt"/>,
    /// with no id. Event names are served as drafted, so the draft must name them canonically.
    /// </summary>
    /// <exception cref="InvalidOperationException">The draft covers a name that is no capability.</exception>
    public static TrackerDefinitionDto From(TrackerDefinitionDraft draft, DateTime createdAt)
    {
        if (!TrackerCoverage.TryNormalise(draft.Covers, out var covers, out var coversError))
            throw new InvalidOperationException(coversError);

        return new()
        {
            Name = draft.Name,
            Description = draft.Description,
            Category = draft.Category,
            Icon = draft.Icon ?? TrackerDefinition.DefaultIcon,
            TriggerEventTypes = [.. draft.TriggerEventTypes ?? []],
            EndEventTypes = [.. draft.EndEventTypes ?? []],
            Covers = [.. TrackerCoverage.Of(draft.Category, (covers ?? []).ToHashSet()).Order()],
            DeviceComponent = draft.DeviceComponent,
            TriggerNotesContains = draft.TriggerNotesContains,
            LifespanHours = draft.LifespanHours,
            LowReservoirUnits = draft.LowReservoirUnits,
            LowReservoirUrgency = draft.LowReservoirUrgency,
            NotificationThresholds =
            [
                .. (draft.NotificationThresholds ?? [])
                    .OrderBy(t => t.DisplayOrder)
                    .Select(t => NotificationThresholdDto.From(t, draft.Mode, draft.LifespanHours)),
            ],
            IsFavorite = draft.IsFavorite,
            DashboardVisibility = draft.DashboardVisibility,
            StartEventType = draft.StartEventType,
            CompletionEventType = draft.CompletionEventType,
            Mode = draft.Mode,
            CreatedAt = createdAt,
        };
    }
}

/// <summary>A <see cref="TrackerTemplate"/> as the tracker wizard reads it.</summary>
public class TrackerTemplateDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The definition a tracker of this template starts as. It is not saved and has no id.</summary>
    public TrackerDefinitionDto Definition { get; set; } = new();
}

public class TrackerPresetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid DefinitionId { get; set; }
    public string DefinitionName { get; set; } = string.Empty;
    public string? DefaultStartNotes { get; set; }
    public DateTime CreatedAt { get; set; }

    public static TrackerPresetDto From(TrackerPreset preset) =>
        new()
        {
            Id = preset.Id,
            Name = preset.Name,
            DefinitionId = preset.DefinitionId,
            DefinitionName = preset.Definition?.Name ?? string.Empty,
            DefaultStartNotes = preset.DefaultStartNotes,
            CreatedAt = preset.CreatedAt,
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

    /// <summary>
    /// Device event types that complete the running instance without starting another.
    /// </summary>
    public List<string>? EndEventTypes { get; set; }

    /// <summary>
    /// <see cref="PumpCapability"/> names the tracked hardware provides.
    /// </summary>
    public List<string>? Covers { get; set; }
    public string? TriggerNotesContains { get; set; }
    public int? LifespanHours { get; set; }

    /// <summary>
    /// For a tracker that holds insulin (a Reservoir category, or one covering Reservoir): units
    /// level below which the synced managed reservoir rule fires. Null = no level rule.
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
    [NotBlankWhenPresent]
    public string? Name { get; set; }
    public string? Description { get; set; }
    public TrackerCategory? Category { get; set; }
    public string? Icon { get; set; }
    public List<string>? TriggerEventTypes { get; set; }

    /// <summary>
    /// Device event types that complete the running instance without starting another. Null keeps
    /// the current list; an empty list clears it.
    /// </summary>
    public List<string>? EndEventTypes { get; set; }

    /// <summary>
    /// <see cref="PumpCapability"/> names the tracked hardware provides. Null keeps the current
    /// list; an empty list clears it.
    /// </summary>
    public List<string>? Covers { get; set; }
    public string? TriggerNotesContains { get; set; }

    /// <summary>
    /// Null keeps the current lifespan, except that a tracker revised to Event mode drops it.
    /// </summary>
    public int? LifespanHours { get; set; }

    /// <summary>
    /// For a tracker that holds insulin (a Reservoir category, or one covering Reservoir): units
    /// level below which the synced managed reservoir rule fires. The tracker editor posts the
    /// whole definition, so for such a definition this value is applied as-is on every update —
    /// null clears the rule (null-means-keep cannot express clearing).
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
    /// Event type to create when tracker is started (for Nightscout compatibility). Applied as
    /// posted: null or blank clears it.
    /// </summary>
    public string? StartEventType { get; set; }

    /// <summary>
    /// Event type to create when tracker is completed (for Nightscout compatibility). Applied as
    /// posted: null or blank clears it.
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
