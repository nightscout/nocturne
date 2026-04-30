using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// CRUD controller for alert rules with nested schedules, escalation steps, and channels.
/// </summary>
/// <remarks>
/// The runtime evaluation pipeline that operates on these rules is documented in
/// <c>docs/diagrams/alert-evaluation-pipeline.mmd</c> — the rendered SVG appears under
/// the Monitoring tag in the Scalar OpenAPI docs (wired via
/// <c>diagrams.yaml</c>'s <c>tags: [Monitoring]</c> entry and
/// <see cref="Configuration.TagDescriptionDocumentTransformer"/>).
/// </remarks>
/// <seealso cref="NocturneDbContext"/>
/// <seealso cref="IAlertReferenceService"/>
/// <seealso cref="Services.Alerts.AlertOrchestrator"/>
[ApiController]
[Authorize]
[Route("api/v4/alert-rules")]
public class AlertRulesController : ControllerBase
{
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly IAlertReferenceService _referenceService;
    private readonly ILogger<AlertRulesController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertRulesController"/>.
    /// </summary>
    public AlertRulesController(
        IDbContextFactory<NocturneDbContext> contextFactory,
        IAlertReferenceService referenceService,
        ILogger<AlertRulesController> logger)
    {
        _contextFactory = contextFactory;
        _referenceService = referenceService;
        _logger = logger;
    }

    /// <summary>
    /// List all alert rules for the current tenant with schedules and escalation steps.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AlertRuleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AlertRuleResponse>>> GetRules(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var rules = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Schedules)
                .ThenInclude(s => s.EscalationSteps)
                    .ThenInclude(es => es.Channels)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);

        return Ok(rules.Select(MapToResponse).ToList());
    }

    /// <summary>
    /// Get a single alert rule with full schedule/escalation tree.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> GetRule(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var rule = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Schedules)
                .ThenInclude(s => s.EscalationSteps)
                    .ThenInclude(es => es.Channels)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (rule is null)
            return NotFound();

        return Ok(MapToResponse(rule));
    }

    /// <summary>
    /// Create an alert rule with nested schedules, escalation steps, and channels.
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetRules"])]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertRuleResponse>> CreateRule(
        [FromBody] CreateAlertRuleRequest request, CancellationToken ct)
    {
        // No cycle detection on create: the new id is server-generated, so the proposed tree
        // cannot reference an id it doesn't yet know. Cycles can only be introduced via PUT.
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var tenantId = db.TenantId;

        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = request.Name,
            Description = request.Description,
            ConditionType = request.ConditionType,
            ConditionParams = request.ConditionParams is not null
                ? JsonSerializer.Serialize(request.ConditionParams)
                : "{}",
            IsEnabled = request.IsEnabled,
            SortOrder = request.SortOrder,
            Severity = request.Severity ?? AlertRuleSeverity.Warning,
            AutoResolveEnabled = request.AutoResolveEnabled,
            AutoResolveParams = request.AutoResolveParams is not null
                ? JsonSerializer.Serialize(request.AutoResolveParams)
                : null,
            ClientConfiguration = request.ClientConfiguration is not null
                ? JsonSerializer.Serialize(request.ClientConfiguration)
                : "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        if (request.Schedules is { Count: > 0 })
        {
            foreach (var schedReq in request.Schedules)
            {
                var schedule = CreateScheduleEntity(schedReq, rule.Id, tenantId);
                rule.Schedules.Add(schedule);
            }
        }
        else
        {
            // Auto-create a default schedule
            rule.Schedules.Add(new AlertScheduleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AlertRuleId = rule.Id,
                Name = "Default",
                IsDefault = true,
                Timezone = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);

        // Reload with includes for response
        var created = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Schedules)
                .ThenInclude(s => s.EscalationSteps)
                    .ThenInclude(es => es.Channels)
            .FirstAsync(r => r.Id == rule.Id, ct);

        return CreatedAtAction(nameof(GetRule), new { id = created.Id }, MapToResponse(created));
    }

    /// <summary>
    /// Update an alert rule.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetRules", "GetRule"])]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertRuleResponse>> UpdateRule(
        Guid id, [FromBody] UpdateAlertRuleRequest request, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var rule = await db.AlertRules
            .Include(r => r.Schedules)
                .ThenInclude(s => s.EscalationSteps)
                    .ThenInclude(es => es.Channels)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (rule is null)
            return NotFound();

        // Cycle detection runs after the existence check so a non-existent id always 404s
        // rather than masking with a 400 when the proposed tree happens to walk a cycle.
        var rootForCycle = TryDeserializeRoot(request.ConditionType, request.ConditionParams);
        if (rootForCycle is not null
            && await _referenceService.DetectCycleAsync(id, rootForCycle, ct))
        {
            return BadRequest("Cyclical alert_state reference detected.");
        }

        var tenantId = db.TenantId;

        rule.Name = request.Name;
        rule.Description = request.Description;
        rule.ConditionType = request.ConditionType;
        rule.ConditionParams = request.ConditionParams is not null
            ? JsonSerializer.Serialize(request.ConditionParams)
            : "{}";
        rule.IsEnabled = request.IsEnabled;
        rule.SortOrder = request.SortOrder;
        rule.Severity = request.Severity ?? AlertRuleSeverity.Warning;
        rule.AutoResolveEnabled = request.AutoResolveEnabled;
        rule.AutoResolveParams = request.AutoResolveParams is not null
            ? JsonSerializer.Serialize(request.AutoResolveParams)
            : null;
        rule.ClientConfiguration = request.ClientConfiguration is not null
            ? JsonSerializer.Serialize(request.ClientConfiguration)
            : "{}";
        rule.UpdatedAt = DateTime.UtcNow;

        if (request.Schedules is not null)
        {
            // Remove old schedules (cascade deletes steps and channels)
            db.AlertSchedules.RemoveRange(rule.Schedules);

            rule.Schedules.Clear();
            foreach (var schedReq in request.Schedules)
            {
                var schedule = CreateScheduleEntity(schedReq, rule.Id, tenantId);
                rule.Schedules.Add(schedule);
            }
        }

        await db.SaveChangesAsync(ct);

        // Reload for response
        var updated = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Schedules)
                .ThenInclude(s => s.EscalationSteps)
                    .ThenInclude(es => es.Channels)
            .FirstAsync(r => r.Id == id, ct);

        return Ok(MapToResponse(updated));
    }

    /// <summary>
    /// Delete an alert rule (cascades to schedules, steps, channels).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetRules"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ReferencingRulesResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
            return NotFound();

        // Refuse to break the alert_state graph: if any other rule references this one, the
        // caller must update or delete those first. Returning the offending ids lets the FE
        // either link to them or offer a cascade-delete.
        var referencing = await _referenceService.FindReferencingRulesAsync(id, ct);
        if (referencing.Count > 0)
        {
            return Conflict(new ReferencingRulesResponse(referencing));
        }

        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Toggle an alert rule enabled/disabled.
    /// </summary>
    [HttpPatch("{id:guid}/toggle")]
    [RemoteCommand(Invalidates = ["GetRules", "GetRule"])]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> ToggleRule(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var rule = await db.AlertRules
            .Include(r => r.Schedules)
                .ThenInclude(s => s.EscalationSteps)
                    .ThenInclude(es => es.Channels)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (rule is null)
            return NotFound();

        rule.IsEnabled = !rule.IsEnabled;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(MapToResponse(rule));
    }

    #region Helpers

    private static AlertScheduleEntity CreateScheduleEntity(
        CreateAlertScheduleRequest req, Guid ruleId, Guid tenantId)
    {
        var schedule = new AlertScheduleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            AlertRuleId = ruleId,
            Name = req.Name ?? "Default",
            IsDefault = req.IsDefault,
            DaysOfWeek = req.DaysOfWeek is not null
                ? JsonSerializer.Serialize(req.DaysOfWeek)
                : null,
            StartTime = req.StartTime is not null ? TimeOnly.Parse(req.StartTime) : null,
            EndTime = req.EndTime is not null ? TimeOnly.Parse(req.EndTime) : null,
            Timezone = req.Timezone ?? "UTC",
            QuietHoursStart = req.QuietHoursEnabled && req.QuietHoursStart is not null
                ? TimeOnly.Parse(req.QuietHoursStart)
                : null,
            QuietHoursEnd = req.QuietHoursEnabled && req.QuietHoursEnd is not null
                ? TimeOnly.Parse(req.QuietHoursEnd)
                : null,
            QuietHoursOverrideCritical = req.QuietHoursOverrideCritical,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        if (req.EscalationSteps is not null)
        {
            foreach (var stepReq in req.EscalationSteps)
            {
                var step = new AlertEscalationStepEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    AlertScheduleId = schedule.Id,
                    StepOrder = stepReq.StepOrder,
                    DelaySeconds = stepReq.DelaySeconds,
                    CreatedAt = DateTime.UtcNow,
                };

                if (stepReq.Channels is not null)
                {
                    foreach (var chReq in stepReq.Channels)
                    {
                        step.Channels.Add(new AlertStepChannelEntity
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = tenantId,
                            EscalationStepId = step.Id,
                            ChannelType = chReq.ChannelType,
                            Destination = chReq.Destination,
                            DestinationLabel = chReq.DestinationLabel,
                            CreatedAt = DateTime.UtcNow,
                        });
                    }
                }

                schedule.EscalationSteps.Add(step);
            }
        }

        return schedule;
    }

    private static AlertRuleResponse MapToResponse(AlertRuleEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Description = entity.Description,
        ConditionType = entity.ConditionType,
        ConditionParams = DeserializeJson(entity.ConditionParams),
        IsEnabled = entity.IsEnabled,
        SortOrder = entity.SortOrder,
        Severity = entity.Severity,
        AutoResolveEnabled = entity.AutoResolveEnabled,
        AutoResolveParams = entity.AutoResolveParams is null
            ? null
            : DeserializeJson(entity.AutoResolveParams),
        ClientConfiguration = DeserializeJson(entity.ClientConfiguration),
        Schedules = entity.Schedules
            .Select(s => new AlertScheduleResponse
            {
                Id = s.Id,
                Name = s.Name,
                IsDefault = s.IsDefault,
                DaysOfWeek = s.DaysOfWeek is not null
                    ? JsonSerializer.Deserialize<int[]>(s.DaysOfWeek)
                    : null,
                StartTime = s.StartTime?.ToString("HH:mm"),
                EndTime = s.EndTime?.ToString("HH:mm"),
                Timezone = s.Timezone,
                QuietHoursStart = s.QuietHoursStart?.ToString("HH:mm"),
                QuietHoursEnd = s.QuietHoursEnd?.ToString("HH:mm"),
                QuietHoursOverrideCritical = s.QuietHoursOverrideCritical,
                EscalationSteps = s.EscalationSteps
                    .OrderBy(es => es.StepOrder)
                    .Select(es => new AlertEscalationStepResponse
                    {
                        Id = es.Id,
                        StepOrder = es.StepOrder,
                        DelaySeconds = es.DelaySeconds,
                        Channels = es.Channels
                            .Select(ch => new AlertStepChannelResponse
                            {
                                Id = ch.Id,
                                ChannelType = ch.ChannelType,
                                Destination = ch.Destination,
                                DestinationLabel = ch.DestinationLabel,
                            })
                            .ToList(),
                    })
                    .ToList(),
            })
            .ToList(),
    };

    private static object DeserializeJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return new { };
        }
    }

    /// <summary>
    /// Reconstructs a <see cref="ConditionNode"/> from the controller's
    /// <c>ConditionType</c> + <c>ConditionParams</c> request shape so the reference walker can
    /// inspect the proposed tree before persisting. Returns null if the payload can't be
    /// deserialised — cycle detection then no-ops and the request still passes validation
    /// (the existing rule-shape validator catches malformed payloads with a clearer error).
    /// </summary>
    private static ConditionNode? TryDeserializeRoot(AlertConditionType type, object? conditionParams)
    {
        if (conditionParams is null) return null;
        try
        {
            var json = JsonSerializer.Serialize(conditionParams);
            return type switch
            {
                AlertConditionType.Composite => new ConditionNode("composite",
                    Composite: JsonSerializer.Deserialize<CompositeCondition>(json, ReferenceJsonOptions)),
                AlertConditionType.Not => new ConditionNode("not",
                    Not: JsonSerializer.Deserialize<NotCondition>(json, ReferenceJsonOptions)),
                AlertConditionType.Sustained => new ConditionNode("sustained",
                    Sustained: JsonSerializer.Deserialize<SustainedCondition>(json, ReferenceJsonOptions)),
                AlertConditionType.AlertState => new ConditionNode("alert_state",
                    AlertState: JsonSerializer.Deserialize<AlertStateCondition>(json, ReferenceJsonOptions)),
                _ => new ConditionNode(type.ToString().ToLowerInvariant()),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions ReferenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    #endregion
}

/// <summary>
/// 409 response body returned by <c>DELETE /api/v4/alert-rules/{id}</c> when other rules
/// reference the target via <c>alert_state</c>. The FE uses this to either link to those
/// rules or offer a cascade-delete confirmation.
/// </summary>
public record ReferencingRulesResponse(IReadOnlyList<Guid> ReferencingRuleIds);

#region DTOs

public class AlertRuleResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;
    public object ConditionParams { get; set; } = new { };
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;
    public bool AutoResolveEnabled { get; set; }
    public object? AutoResolveParams { get; set; }
    public object ClientConfiguration { get; set; } = new { };
    public List<AlertScheduleResponse> Schedules { get; set; } = [];
}

public class AlertScheduleResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public int[]? DaysOfWeek { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string Timezone { get; set; } = "UTC";
    public string? QuietHoursStart { get; set; }
    public string? QuietHoursEnd { get; set; }
    public bool QuietHoursOverrideCritical { get; set; }
    public List<AlertEscalationStepResponse> EscalationSteps { get; set; } = [];
}

public class AlertEscalationStepResponse
{
    public Guid Id { get; set; }
    public int StepOrder { get; set; }
    public int DelaySeconds { get; set; }
    public List<AlertStepChannelResponse> Channels { get; set; } = [];
}

public class AlertStepChannelResponse
{
    public Guid Id { get; set; }
    public ChannelType ChannelType { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string? DestinationLabel { get; set; }
}

public class CreateAlertRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;
    public object? ConditionParams { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public AlertRuleSeverity? Severity { get; set; }
    public bool AutoResolveEnabled { get; set; }
    public object? AutoResolveParams { get; set; }
    public object? ClientConfiguration { get; set; }
    public List<CreateAlertScheduleRequest>? Schedules { get; set; }
}

public class UpdateAlertRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;
    public object? ConditionParams { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public AlertRuleSeverity? Severity { get; set; }
    public bool AutoResolveEnabled { get; set; }
    public object? AutoResolveParams { get; set; }
    public object? ClientConfiguration { get; set; }
    public List<CreateAlertScheduleRequest>? Schedules { get; set; }
}

public class CreateAlertScheduleRequest
{
    public string? Name { get; set; }
    public bool IsDefault { get; set; }
    public int[]? DaysOfWeek { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? Timezone { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public string? QuietHoursStart { get; set; }
    public string? QuietHoursEnd { get; set; }
    public bool QuietHoursOverrideCritical { get; set; } = true;
    public List<CreateAlertEscalationStepRequest>? EscalationSteps { get; set; }
}

public class CreateAlertEscalationStepRequest
{
    public int StepOrder { get; set; }
    public int DelaySeconds { get; set; }
    public List<CreateAlertStepChannelRequest>? Channels { get; set; }
}

public class CreateAlertStepChannelRequest
{
    public ChannelType ChannelType { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string? DestinationLabel { get; set; }
}

#endregion
