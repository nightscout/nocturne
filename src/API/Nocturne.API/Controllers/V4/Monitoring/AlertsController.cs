using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Controller for active alert state, history, and acknowledgement.
/// </summary>
/// <remarks>
/// <para>
/// Acknowledging, snoozing and recording a delivery outcome all silence or close an excursion, so
/// every write action here requires <see cref="Scope.AlertsReadWrite"/>, as does
/// <see cref="GetPendingDeliveries"/>, which returns other recipients' destinations and payloads
/// to the delivery adapters; a guest-link session, which may hold <c>alerts.read</c>, reaches none
/// of them. <see cref="AcknowledgeExcursion"/> additionally accepts
/// <see cref="Scope.DeviceNotify"/>, because for a caller without <c>alerts.readwrite</c> it mutes
/// the excursion for that caller alone. See the note on that action.
/// </para>
/// <para>
/// The one alert controller without a class <c>[Authorize]</c>: a public share link is an
/// unauthenticated principal that may hold <c>alerts.read</c> to show the alarm that is firing and
/// record that it saw it, and <c>[Authorize]</c> would answer it 401. The actions a share never
/// reaches carry <c>[Authorize]</c> themselves, so they keep the authenticated default policy rather
/// than the permission-trie fallback. The other alert controllers keep <c>[Authorize]</c> and
/// <see cref="DenyNoAccountViewerAttribute"/>: they serve configuration.
/// </para>
/// </remarks>
/// <seealso cref="IAlertAcknowledgementService"/>
/// <seealso cref="IAlertDeliveryService"/>
[ApiController]
[Tags("Monitoring")]
[Route("api/v4/alerts")]
public class AlertsController : ControllerBase
{
    private readonly INocturneDbContextFactory _contextFactory;
    private readonly IAlertAcknowledgementService _acknowledgementService;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly IAlertSnoozeService _snoozeService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<AlertsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertsController"/>.
    /// </summary>
    /// <param name="contextFactory">Tenant-scoped factory for creating <see cref="NocturneDbContext"/> instances.</param>
    /// <param name="acknowledgementService">Service for acknowledging alert excursions.</param>
    /// <param name="deliveryService">Service for marking alert delivery outcomes.</param>
    /// <param name="snoozeService">Service for snoozing alert instances.</param>
    /// <param name="tenantAccessor">Accessor for the current request tenant context.</param>
    /// <param name="logger">Logger instance.</param>
    public AlertsController(
        INocturneDbContextFactory contextFactory,
        IAlertAcknowledgementService acknowledgementService,
        IAlertDeliveryService deliveryService,
        IAlertSnoozeService snoozeService,
        ITenantAccessor tenantAccessor,
        ILogger<AlertsController> logger)
    {
        _contextFactory = contextFactory;
        _acknowledgementService = acknowledgementService;
        _deliveryService = deliveryService;
        _snoozeService = snoozeService;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// List active (unresolved) excursions for the current tenant.
    /// </summary>
    [HttpGet("active")]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<ActiveExcursionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ActiveExcursionResponse>>> GetActiveAlerts(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var excursions = await db.AlertExcursions
            .AsNoTracking()
            .Include(e => e.AlertRule)
            .Include(e => e.Instances)
            .Where(e => e.EndedAt == null)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);

        var subjectId = HttpContext.GetSubjectId();
        var mutedByCaller = subjectId is { } id
            ? await db.AlertExcursionMutes
                .AsNoTracking()
                .Where(m => m.SubjectId == id)
                .Select(m => m.AlertExcursionId)
                .ToHashSetAsync(ct)
            : new HashSet<Guid>();

        var namesAcknowledger = NamesMembers();
        var receipts = await AlertReceiptService.ReadAsync(
            db, excursions.Select(e => e.Id).ToList(), ReceiptsVisibleOnlyFor(), ct);
        var now = DateTime.UtcNow;
        var result = excursions.Select(e => new ActiveExcursionResponse
        {
            Id = e.Id,
            AlertRuleId = e.AlertRuleId,
            RuleName = e.AlertRule?.Name ?? string.Empty,
            ConditionType = e.AlertRule?.ConditionType ?? AlertConditionType.Threshold,
            Severity = e.AlertRule?.Severity ?? AlertRuleSeverity.Warning,
            StartedAt = e.StartedAt,
            AcknowledgedAt = e.AcknowledgedAt,
            AcknowledgedBy = namesAcknowledger ? e.AcknowledgedBy : null,
            MutedByCaller = mutedByCaller.Contains(e.Id),
            Receipts = AlertReceiptResponse.From(receipts[e.Id]),
            HysteresisStartedAt = e.HysteresisStartedAt,
            SnoozedUntil = e.Instances
                .Where(i => i.ResolvedAt == null)
                .Select(i => AlertSnooze.ActiveUntil(i.SnoozedUntil, now))
                .Max(),
            ActiveInstances = e.Instances
                .Where(i => i.ResolvedAt == null)
                .Select(i => new ActiveInstanceResponse
                {
                    Id = i.Id,
                    Status = i.Status,
                    TriggeredAt = i.TriggeredAt,
                    SuppressionReason = i.SuppressionReason,
                    SnoozedUntil = AlertSnooze.ActiveUntil(i.SnoozedUntil, now),
                    SnoozeCount = i.SnoozeCount,
                })
                .ToList(),
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Get paginated history of resolved excursions. Test fires are excluded
    /// by default; pass <paramref name="includeTest"/> = true to include them.
    /// </summary>
    /// <remarks>
    /// <paramref name="pageSize"/> is capped at <see cref="V4ReadLimits.MaxOrdinalPageSize"/> and
    /// <paramref name="page"/> at the page that reaches the last record within
    /// <see cref="V4ReadLimits.MaxPageSize"/>; both are clamped rather than rejected.
    /// </remarks>
    [HttpGet("history")]
    [DenyNoAccountViewer]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertHistoryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertHistoryResponse>> GetAlertHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? alertRuleId = null,
        [FromQuery] bool includeTest = false,
        CancellationToken ct = default)
    {
        pageSize = V4ReadLimits.ClampPageSize(pageSize);
        page = V4ReadLimits.ClampPageNumber(page, pageSize);

        await using var db = await _contextFactory.CreateAsync(ct);

        // EndedAt <= now: history is completed excursions only. device_action test fires carry
        // a short future EndedAt (AlertDeliveryService.TestFireAsync) so they read as live in
        // the active-intents snapshot; they join history once that window lapses.
        var now = DateTime.UtcNow;
        var query = db.AlertExcursions
            .AsNoTracking()
            .Include(e => e.AlertRule)
            .Where(e => e.EndedAt != null && e.EndedAt <= now);

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
                Severity = e.AlertRule != null ? e.AlertRule.Severity : AlertRuleSeverity.Warning,
                e.StartedAt,
                EndedAt = e.EndedAt!.Value,
                e.AcknowledgedAt,
                e.AcknowledgedBy,
                IsTest = e.Instances.Any(i => i.IsTest) && !e.Instances.Any(i => !i.IsTest),
            })
            .ToListAsync(ct);

        var namesAcknowledger = NamesMembers();
        var receipts = await AlertReceiptService.ReadAsync(
            db, items.Select(e => e.Id).ToList(), ReceiptsVisibleOnlyFor(), ct);
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
                Severity = e.Severity,
                StartedAt = e.StartedAt,
                EndedAt = e.EndedAt,
                AcknowledgedAt = e.AcknowledgedAt,
                AcknowledgedBy = namesAcknowledger ? e.AcknowledgedBy : null,
                IsTest = e.IsTest,
                Receipts = AlertReceiptResponse.From(receipts[e.Id]),
            }).ToList(),
        };

        return Ok(result);
    }

    /// <summary>
    /// Whether the caller may see which member acknowledged an alert. A share or a guest link may see
    /// that an alert was acknowledged, not by whom.
    /// </summary>
    private bool NamesMembers() => !DenyNoAccountViewerAttribute.Refuses(HttpContext);

    /// <summary>
    /// The actor whose receipts alone the caller may see: a share or guest link sees its own and no
    /// one else's, a member sees all of them (null).
    /// </summary>
    private AlertReceiptActor? ReceiptsVisibleOnlyFor() =>
        NamesMembers()
            ? null
            : AlertReceiptActor.Of(HttpContext) ?? new AlertReceiptActor(AlertReceiptActorKind.PublicLink, Guid.Empty);

    /// <summary>
    /// Record that the caller saw an alarm that is firing. It changes nothing about the alert: it
    /// is not an acknowledgement and mutes nothing, for the caller or anyone else, and recording it
    /// again keeps the first. Members with an account see who recorded one, and when.
    /// </summary>
    /// <remarks>
    /// The one write a public share link may make. <see cref="RequireScopeAttribute"/> admits an
    /// unauthenticated caller to a read requirement, and the share principal holds
    /// <see cref="Scope.AlertsRead"/> only when its owner shared alerts. Who recorded it comes from
    /// the credential (<see cref="AlertReceiptActor.Of"/>), never from the request.
    /// </remarks>
    [HttpPost("excursions/{excursionId:guid}/seen")]
    [RequireScope(Scope.AlertsRead)]
    [EnableRateLimiting(ServiceRegistrationExtensions.NoAccountAlertsRateLimitPolicy)]
    [RemoteCommand(Invalidates = ["GetActiveAlerts"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> MarkSeen(
        Guid excursionId, [FromServices] AlertReceiptService receipts, CancellationToken ct)
    {
        if (AlertReceiptActor.Of(HttpContext) is not { } actor)
            return Forbid();

        return await receipts.RecordAsync(excursionId, actor, ct) ? NoContent() : NotFound();
    }

    /// <inheritdoc cref="IAlertAcknowledgementService.AcknowledgeAllAsync"/>
    [HttpPost("acknowledge")]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetActiveAlerts"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Acknowledge(
        [FromBody] AcknowledgeRequest request,
        [FromServices] AlertReceiptService receipts, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;

        List<Guid> open;
        await using (var db = await _contextFactory.CreateAsync(ct))
        {
            open = await db.AlertExcursions.AsNoTracking()
                .Where(e => e.EndedAt == null)
                .Select(e => e.Id)
                .ToListAsync(ct);
        }

        await _acknowledgementService.AcknowledgeAllAsync(
            tenantId,
            User.ResolveAcknowledger(request.AcknowledgedBy),
            ct);

        // As for one excursion: acknowledging every alarm is also seeing each of them.
        if (AlertReceiptActor.Of(HttpContext) is { Kind: AlertReceiptActorKind.Member } member)
        {
            foreach (var excursionId in open)
                await receipts.RecordAsync(excursionId, member, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Acknowledge a single alert excursion. A caller with <c>alerts.readwrite</c> acknowledges it
    /// for everyone; anyone else mutes it for themselves. The response says which applied.
    /// Returns 404 when the excursion does not exist for the current tenant.
    /// </summary>
    /// <remarks>
    /// Accepts <see cref="Scope.DeviceNotify"/> as well as <see cref="Scope.AlertsReadWrite"/>:
    /// this is the endpoint behind the action on a device toast, and a registered client device's
    /// grant carries the device capability scopes rather than the alert data scope. Every member
    /// holding a permission reaches it (see <see cref="Scope.MemberPersonalScopes"/>); what the
    /// call does is decided by <see cref="IAlertAcknowledgementService.AcknowledgeExcursionAsync"/>.
    /// A guest link reaches neither scope, its grant being capped at
    /// <see cref="Scope.AllowedGuestScopes"/>.
    /// </remarks>
    [HttpPost("excursions/{excursionId:guid}/acknowledge")]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite, Scope.DeviceNotify)]
    [RemoteCommand(Invalidates = ["GetActiveAlerts"])]
    [ProducesResponseType(typeof(AcknowledgeExcursionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AcknowledgeExcursionResponse>> AcknowledgeExcursion(
        Guid excursionId, [FromBody] AcknowledgeRequest request,
        [FromServices] AlertReceiptService receipts, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;

        // The acknowledgement service silently no-ops on an unknown excursion, so
        // resolve existence here (tenant-scoped context — another tenant's
        // excursion is invisible and yields 404).
        await using var db = await _contextFactory.CreateAsync(ct);
        var exists = await db.AlertExcursions
            .AsNoTracking()
            .AnyAsync(e => e.Id == excursionId, ct);

        if (!exists)
            return NotFound();

        var outcome = await _acknowledgementService.AcknowledgeExcursionAsync(
            tenantId,
            excursionId,
            User.ResolveAcknowledger(request.AcknowledgedBy),
            HttpContext.GetAlertAcknowledgementAuthority(),
            broadcast: true,
            ct);

        // Acknowledging is also seeing it, so the receipts list everyone who did either.
        if (AlertReceiptActor.Of(HttpContext) is { Kind: AlertReceiptActorKind.Member } member)
            await receipts.RecordAsync(excursionId, member, ct);

        return Ok(new AcknowledgeExcursionResponse { Outcome = outcome });
    }

    /// <summary>
    /// Snooze an alert instance: no notification is sent for it, on any channel, until
    /// <see cref="SnoozeRequest.Minutes"/> from now. The alert stays active and unacknowledged,
    /// and re-notifies when the snooze lapses if it is still firing. Snoozing again replaces the
    /// window. Returns 409 when the instance is already resolved or has used the rule's
    /// <c>snooze.maxCount</c> (<see cref="Nocturne.API.Services.Alerts.SmartSnoozeConfig"/>), a count
    /// shared with the sweep's smart-snooze extensions.
    /// </summary>
    /// <remarks>
    /// Accepts <see cref="Scope.AlertsAcknowledge"/> as well as <see cref="Scope.AlertsReadWrite"/>:
    /// a snooze silences the alert for everyone and changes nothing else, which is all a Nightscout
    /// token holding <c>notifications:*:ack</c> may do.
    /// </remarks>
    /// <seealso cref="AlertSnooze"/>
    [HttpPost("instances/{instanceId:guid}/snooze")]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite, Scope.AlertsAcknowledge)]
    [RemoteCommand(Invalidates = ["GetActiveAlerts"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> SnoozeInstance(
        Guid instanceId, [FromBody] SnoozeRequest request, CancellationToken ct)
    {
        var outcome = await _snoozeService.SnoozeAsync(instanceId, request.Minutes, ct);

        return outcome.Result switch
        {
            SnoozeResult.Snoozed => NoContent(),
            SnoozeResult.NotFound => NotFound(),
            SnoozeResult.NotActive => Problem(detail: "Alert is no longer active", statusCode: 409, title: "Conflict"),
            _ => Problem(detail: "Maximum snooze count reached", statusCode: 409, title: "Conflict"),
        };
    }

    /// <inheritdoc cref="IAlertDeliveryService.MarkDeliveredAsync"/>
    [HttpPost("deliveries/{deliveryId:guid}/delivered")]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
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
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
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
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<PendingDeliveryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<PendingDeliveryResponse>>> GetPendingDeliveries(
        [FromQuery] ChannelType[] channelType, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

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
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;
    public DateTime StartedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// True when the caller muted this excursion for themselves. It is still unacknowledged for
    /// everyone else.
    /// </summary>
    public bool MutedByCaller { get; set; }

    public DateTime? HysteresisStartedAt { get; set; }

    /// <summary>
    /// Latest <see cref="ActiveInstanceResponse.SnoozedUntil"/> among the active instances; null
    /// when none is snoozed.
    /// </summary>
    public DateTime? SnoozedUntil { get; set; }

    public List<ActiveInstanceResponse> ActiveInstances { get; set; } = [];

    /// <summary>
    /// Who has recorded seeing it, oldest first: every viewer for a caller with an account, only its
    /// own for a share or guest link.
    /// </summary>
    public List<AlertReceiptResponse> Receipts { get; set; } = [];
}

/// <summary>One viewer's record that they saw an alarm.</summary>
public class AlertReceiptResponse
{
    public AlertReceiptActorKind ActorKind { get; set; }

    /// <summary>
    /// The member's name, the guest link's label or the clock face's name; null for the public link.
    /// </summary>
    public string? ActorLabel { get; set; }

    public DateTime SeenAt { get; set; }

    internal static List<AlertReceiptResponse> From(IEnumerable<AlertReceipt> receipts) =>
        receipts.Select(r => new AlertReceiptResponse
        {
            ActorKind = r.ActorKind,
            ActorLabel = r.ActorLabel,
            SeenAt = r.SeenAt,
        }).ToList();
}

public class ActiveInstanceResponse
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }
    /// <summary>One of <c>"dnd"</c> when delivery was suppressed at fire time, otherwise null.</summary>
    public string? SuppressionReason { get; set; }

    /// <summary>
    /// End of the snooze in force, or null when the instance is not snoozed — including a snooze
    /// that has lapsed and not yet been swept (<see cref="AlertSnooze.ActiveUntil"/>).
    /// </summary>
    public DateTime? SnoozedUntil { get; set; }

    public int SnoozeCount { get; set; }
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

    /// <summary>
    /// The rule's severity, so a history row can be read the same way as a live
    /// one rather than showing every past fire identically.
    /// </summary>
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;

    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }

    /// <summary>True when every instance of this excursion was a test fire.</summary>
    public bool IsTest { get; set; }

    /// <inheritdoc cref="ActiveExcursionResponse.Receipts"/>
    public List<AlertReceiptResponse> Receipts { get; set; } = [];
}

public class AcknowledgeRequest
{
    public string? AcknowledgedBy { get; set; }
}

public class AcknowledgeExcursionResponse
{
    /// <summary>
    /// <c>acknowledged</c> when escalation stopped for everyone, <c>muted</c> when it stopped for
    /// the caller only, <c>closed</c> when the excursion had already ended.
    /// </summary>
    public AlertAcknowledgementOutcome Outcome { get; set; }
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
