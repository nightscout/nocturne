using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Controller for active alert state, history, and acknowledgement.
/// </summary>
/// <seealso cref="IAlertAcknowledgementService"/>
/// <seealso cref="IAlertDeliveryService"/>
[ApiController]
[Authorize]
[Route("api/v4/alerts")]
public class AlertsController : ControllerBase
{
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly IAlertAcknowledgementService _acknowledgementService;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<AlertsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertsController"/>.
    /// </summary>
    /// <param name="contextFactory">Factory for creating <see cref="NocturneDbContext"/> instances.</param>
    /// <param name="acknowledgementService">Service for acknowledging alert excursions.</param>
    /// <param name="deliveryService">Service for marking alert delivery outcomes.</param>
    /// <param name="tenantAccessor">Accessor for the current request tenant context.</param>
    /// <param name="logger">Logger instance.</param>
    public AlertsController(
        IDbContextFactory<NocturneDbContext> contextFactory,
        IAlertAcknowledgementService acknowledgementService,
        IAlertDeliveryService deliveryService,
        ITenantAccessor tenantAccessor,
        ILogger<AlertsController> logger)
    {
        _contextFactory = contextFactory;
        _acknowledgementService = acknowledgementService;
        _deliveryService = deliveryService;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// List active (unresolved) excursions for the current tenant.
    /// </summary>
    [HttpGet("active")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<ActiveExcursionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ActiveExcursionResponse>>> GetActiveAlerts(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var excursions = await db.AlertExcursions
            .AsNoTracking()
            .Include(e => e.AlertRule)
            .Include(e => e.Instances)
            .Where(e => e.EndedAt == null)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);

        var result = excursions.Select(e => new ActiveExcursionResponse
        {
            Id = e.Id,
            AlertRuleId = e.AlertRuleId,
            RuleName = e.AlertRule?.Name ?? string.Empty,
            ConditionType = e.AlertRule?.ConditionType ?? AlertConditionType.Threshold,
            StartedAt = e.StartedAt,
            AcknowledgedAt = e.AcknowledgedAt,
            AcknowledgedBy = e.AcknowledgedBy,
            HysteresisStartedAt = e.HysteresisStartedAt,
            ActiveInstances = e.Instances
                .Where(i => i.ResolvedAt == null)
                .Select(i => new ActiveInstanceResponse
                {
                    Id = i.Id,
                    Status = i.Status,
                    TriggeredAt = i.TriggeredAt,
                    SuppressionReason = i.SuppressionReason,
                })
                .ToList(),
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Get paginated history of resolved excursions. Test fires are excluded
    /// by default; pass <paramref name="includeTest"/> = true to include them.
    /// </summary>
    [HttpGet("history")]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertHistoryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertHistoryResponse>> GetAlertHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? alertRuleId = null,
        [FromQuery] bool includeTest = false,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 100) pageSize = 100;

        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var query = db.AlertExcursions
            .AsNoTracking()
            .Include(e => e.AlertRule)
            .Where(e => e.EndedAt != null);

        if (alertRuleId.HasValue)
            query = query.Where(e => e.AlertRuleId == alertRuleId.Value);

        // A test-fire produces an excursion whose only instance is IsTest=true.
        // Filter by absence of any non-test instance to drop them.
        if (!includeTest)
            query = query.Where(e => e.Instances.Any(i => !i.IsTest));

        var ordered = query.OrderByDescending(e => e.EndedAt);

        var totalCount = await ordered.CountAsync(ct);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.AlertRuleId,
                RuleName = e.AlertRule != null ? e.AlertRule.Name : string.Empty,
                ConditionType = e.AlertRule != null ? e.AlertRule.ConditionType : AlertConditionType.Threshold,
                e.StartedAt,
                EndedAt = e.EndedAt!.Value,
                e.AcknowledgedAt,
                e.AcknowledgedBy,
                IsTest = e.Instances.Any(i => i.IsTest) && !e.Instances.Any(i => !i.IsTest),
            })
            .ToListAsync(ct);

        var result = new AlertHistoryResponse
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize),
            Items = items.Select(e => new HistoryExcursionResponse
            {
                Id = e.Id,
                AlertRuleId = e.AlertRuleId,
                RuleName = e.RuleName,
                ConditionType = e.ConditionType,
                StartedAt = e.StartedAt,
                EndedAt = e.EndedAt,
                AcknowledgedAt = e.AcknowledgedAt,
                AcknowledgedBy = e.AcknowledgedBy,
                IsTest = e.IsTest,
            }).ToList(),
        };

        return Ok(result);
    }

    /// <inheritdoc cref="IAlertAcknowledgementService.AcknowledgeAllAsync"/>
    [HttpPost("acknowledge")]
    [RemoteCommand(Invalidates = ["GetActiveAlerts"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Acknowledge(
        [FromBody] AcknowledgeRequest request, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;

        await _acknowledgementService.AcknowledgeAllAsync(
            tenantId,
            request.AcknowledgedBy ?? "unknown",
            ct);

        return NoContent();
    }

    /// <summary>
    /// Snooze an alert instance for the specified duration.
    /// </summary>
    [HttpPost("instances/{instanceId:guid}/snooze")]
    [RemoteCommand(Invalidates = ["GetActiveAlerts"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> SnoozeInstance(
        Guid instanceId, [FromBody] SnoozeRequest request, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var instance = await db.AlertInstances
            .Include(i => i.AlertExcursion)
                .ThenInclude(e => e!.AlertRule)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);

        if (instance is null)
            return NotFound();

        var rule = instance.AlertExcursion?.AlertRule;

        using var doc = JsonDocument.Parse(rule?.ClientConfiguration ?? "{}");
        var snoozeSection = doc.RootElement.TryGetProperty("snooze", out var snooze) ? snooze : default;

        var maxCount = 5;
        if (snoozeSection.ValueKind != JsonValueKind.Undefined
            && snoozeSection.TryGetProperty("maxCount", out var maxCountElement)
            && maxCountElement.ValueKind == JsonValueKind.Number)
        {
            maxCount = maxCountElement.GetInt32();
        }

        if (instance.SnoozeCount >= maxCount)
            return Problem(detail: "Maximum snooze count reached", statusCode: 409, title: "Conflict");

        instance.SnoozedUntil = DateTime.UtcNow.AddMinutes(request.Minutes);
        instance.SnoozeCount++;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <inheritdoc cref="IAlertDeliveryService.MarkDeliveredAsync"/>
    [HttpPost("deliveries/{deliveryId:guid}/delivered")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MarkDelivered(
        Guid deliveryId, [FromBody] MarkDeliveredRequest request, CancellationToken ct)
    {
        await _deliveryService.MarkDeliveredAsync(
            deliveryId, request.PlatformMessageId, request.PlatformThreadId, ct);
        return NoContent();
    }

    /// <inheritdoc cref="IAlertDeliveryService.MarkFailedAsync"/>
    [HttpPost("deliveries/{deliveryId:guid}/failed")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MarkFailed(
        Guid deliveryId, [FromBody] MarkFailedRequest request, CancellationToken ct)
    {
        await _deliveryService.MarkFailedAsync(deliveryId, request.Error, ct);
        return NoContent();
    }

    /// <summary>
    /// Get pending deliveries for the specified channel types.
    /// Used by bot/adapter services to poll for work.
    /// </summary>
    [HttpGet("deliveries/pending")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<PendingDeliveryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<PendingDeliveryResponse>>> GetPendingDeliveries(
        [FromQuery] ChannelType[] channelType, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var query = db.AlertDeliveries
            .AsNoTracking()
            .Where(d => d.Status == "pending");

        if (channelType.Length > 0)
            query = query.Where(d => channelType.Contains(d.ChannelType));

        var deliveries = await query
            .OrderBy(d => d.CreatedAt)
            .Select(d => new PendingDeliveryResponse
            {
                Id = d.Id,
                AlertInstanceId = d.AlertInstanceId,
                ChannelType = d.ChannelType,
                Destination = d.Destination,
                Payload = d.Payload,
                CreatedAt = d.CreatedAt,
                RetryCount = d.RetryCount,
            })
            .ToListAsync(ct);

        return Ok(deliveries);
    }
}

#region DTOs

public class ActiveExcursionResponse
{
    public Guid Id { get; set; }
    public Guid AlertRuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;
    public DateTime StartedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? HysteresisStartedAt { get; set; }
    public List<ActiveInstanceResponse> ActiveInstances { get; set; } = [];
}

public class ActiveInstanceResponse
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }
    /// <summary>One of <c>"dnd"</c> when delivery was suppressed at fire time, otherwise null.</summary>
    public string? SuppressionReason { get; set; }
}

public class AlertHistoryResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<HistoryExcursionResponse> Items { get; set; } = [];
}

public class HistoryExcursionResponse
{
    public Guid Id { get; set; }
    public Guid AlertRuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }

    /// <summary>True when every instance of this excursion was a test fire.</summary>
    public bool IsTest { get; set; }
}

public class AcknowledgeRequest
{
    public string? AcknowledgedBy { get; set; }
}

public class SnoozeRequest
{
    public int Minutes { get; set; }
}

public class MarkDeliveredRequest
{
    public string? PlatformMessageId { get; set; }
    public string? PlatformThreadId { get; set; }
}

public class MarkFailedRequest
{
    public string Error { get; set; } = string.Empty;
}

public class PendingDeliveryResponse
{
    public Guid Id { get; set; }
    public Guid AlertInstanceId { get; set; }
    public ChannelType ChannelType { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public int RetryCount { get; set; }
}

#endregion
