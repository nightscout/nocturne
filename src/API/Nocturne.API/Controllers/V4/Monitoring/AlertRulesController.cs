using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Authorization;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// CRUD controller for alert rules. Each rule owns a flat list of delivery channels;
/// time-of-day gating, quiet-hours, and escalation chains are expressed inside the rule's
/// condition tree (<c>time_of_day</c>, <c>do_not_disturb</c>, <c>alert_state</c>) rather
/// than as side-channel schedule/step structures.
/// </summary>
/// <remarks>
/// <para>
/// Every write action requires <see cref="Scope.AlertsReadWrite"/>: a rule decides whether a
/// low-glucose alert reaches anyone, and the class-level <c>[Authorize]</c> alone is satisfied by
/// read-only credentials such as a guest-link session, which holds <c>alerts.read</c>. Both rule
/// reads require <see cref="Scope.AlertsRead"/>, because a rule names its delivery channels' phone
/// numbers and webhook targets.
/// </para>
/// <para>
/// The runtime evaluation pipeline that operates on these rules is documented in
/// <c>docs/diagrams/alert-evaluation-pipeline.mmd</c> — the rendered SVG appears under
/// the Monitoring tag in the Scalar OpenAPI docs (wired via
/// <c>diagrams.yaml</c>'s <c>tags: [Monitoring]</c> entry and
/// <see cref="Configuration.TagDescriptionDocumentTransformer"/>).
/// </para>
/// </remarks>
/// <seealso cref="NocturneDbContext"/>
/// <seealso cref="IAlertReferenceService"/>
/// <seealso cref="Services.Alerts.AlertOrchestrator"/>
[ApiController]
[Tags("Monitoring")]
[Authorize]
[Route("api/v4/alert-rules")]
[DenyNoAccountViewer]
public class AlertRulesController : ControllerBase
{
    private readonly INocturneDbContextFactory _contextFactory;
    private readonly IAlertRepository _alerts;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAlertReferenceService _referenceService;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly IRuleScopeClassifier _scopeClassifier;
    private readonly IAlertRuleConditionValidator _conditionValidator;
    private readonly AlertRuleChannelWriter _channels;
    private readonly AlertRuleRearm _rearm;
    private readonly AlertRuleRetirement _retirement;
    private readonly ILogger<AlertRulesController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertRulesController"/>.
    /// </summary>
    public AlertRulesController(
        INocturneDbContextFactory contextFactory,
        IAlertRepository alerts,
        ITenantAccessor tenantAccessor,
        IAlertReferenceService referenceService,
        IAlertDeliveryService deliveryService,
        IRuleScopeClassifier scopeClassifier,
        IAlertRuleConditionValidator conditionValidator,
        ISecretEncryptionService encryption,
        AlertRuleRearm rearm,
        AlertRuleRetirement retirement,
        ILogger<AlertRulesController> logger)
    {
        _contextFactory = contextFactory;
        _alerts = alerts;
        _tenantAccessor = tenantAccessor;
        _referenceService = referenceService;
        _deliveryService = deliveryService;
        _scopeClassifier = scopeClassifier;
        _conditionValidator = conditionValidator;
        _channels = new AlertRuleChannelWriter(encryption);
        _rearm = rearm;
        _retirement = retirement;
        _logger = logger;
    }

    /// <summary>
    /// List all alert rules for the current tenant with their flat channel list.
    /// </summary>
    [HttpGet]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AlertRuleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AlertRuleResponse>>> GetRules(CancellationToken ct)
    {
        var rules = await _alerts.GetRulesAsync(_tenantAccessor.TenantId, ct);
        return Ok(await ToResponsesAsync(rules, ct));
    }

    /// <summary>
    /// Get a single alert rule with its flat channel list.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> GetRule(Guid id, CancellationToken ct)
    {
        var rule = await _alerts.GetRuleAsync(_tenantAccessor.TenantId, id, ct);
        if (rule is null)
            return NotFound();

        return Ok(await ToResponseAsync(rule, ct));
    }

    /// <summary>
    /// Create an alert rule with a flat channel list.
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetRules"])]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertRuleResponse>> CreateRule(
        [FromBody] CreateAlertRuleRequest request, CancellationToken ct)
    {
        var trees = RequestTrees.From(request.ConditionParams, request.AutoResolveParams, request.ClientConfiguration);
        if (RejectInvalidConditions(request.ConditionType, trees, request.AutoResolveEnabled) is { } invalid)
            return invalid;

        // No cycle detection on create: the new id is server-generated, so the proposed tree
        // cannot reference an id it doesn't yet know. Cycles can only be introduced via PUT.
        await using var db = await _contextFactory.CreateAsync(ct);

        if (await ResolveAndValidateChannelsAsync(request.Channels, AlertRuleChannelWriter.NoStoredRecipients, db, ct) is { } badChannel)
            return badChannel;

        var leaves = RuleTrees.Leaves(request.ConditionType, trees.ConditionParams);
        if (await RejectInvalidTrackerAgeAsync(db, leaves, ct) is { } badTracker)
            return badTracker;

        var tenantId = db.TenantId;

        var conditionParamsJson = trees.ConditionParams;

        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = request.Name,
            Description = request.Description,
            ConditionType = request.ConditionType,
            ConditionParams = conditionParamsJson,
            ScopeClass = _scopeClassifier.Classify(request.ConditionType, conditionParamsJson),
            IsEnabled = request.IsEnabled,
            SortOrder = request.SortOrder,
            Severity = request.Severity ?? AlertRuleSeverity.Warning,
            AllowThroughDnd = request.AllowThroughDnd,
            AutoResolveEnabled = request.AutoResolveEnabled,
            AutoResolveParams = trees.AutoResolveParams,
            ClientConfiguration = trees.ClientConfiguration ?? "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        if (request.Channels is { Count: > 0 })
        {
            var sortIndex = 0;
            foreach (var ch in request.Channels)
            {
                rule.Channels.Add(_channels.Build(ch, rule.Id, tenantId, sortIndex++, AlertRuleChannelWriter.NoRetainedSecrets));
            }
        }

        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetRule), new { id = rule.Id }, await ReadBackAsync(tenantId, rule.Id, ct));
    }

    /// <summary>
    /// Update an alert rule.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetRules", "GetRule"])]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertRuleResponse>> UpdateRule(
        Guid id, [FromBody] UpdateAlertRuleRequest request, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contextFactory, ct);
        var db = unit.Context;

        var rule = await db.AlertRules
            .Include(r => r.Channels)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (rule is null)
            return NotFound();

        var trees = RequestTrees.From(request.ConditionParams, request.AutoResolveParams, request.ClientConfiguration);
        if (RejectInvalidConditions(request.ConditionType, trees, request.AutoResolveEnabled) is { } invalid)
            return invalid;

        if (await ResolveAndValidateChannelsAsync(request.Channels, AlertRuleChannelWriter.InAppRecipientsOf(rule.Channels), db, ct) is { } badChannel)
            return badChannel;

        var leaves = RuleTrees.Leaves(request.ConditionType, trees.ConditionParams);
        if (await RejectInvalidTrackerAgeAsync(db, leaves, ct) is { } badTracker)
            return badTracker;

        if (await _referenceService.DetectCycleAsync(id, RuleTrees.AlertStateTargets(leaves).ToList(), ct))
        {
            return BadRequest("Cyclical alert_state reference detected.");
        }

        var tenantId = db.TenantId;

        var conditionParamsJson = trees.ConditionParams;

        // A tree equal as JSON keeps its stored text, so an edit that leaves it alone is not a
        // new condition version.
        var sameBody = ConditionTreeEquality.Same(rule.ConditionParams, conditionParamsJson);
        var sameAutoResolve = ConditionTreeEquality.Same(rule.AutoResolveParams, trees.AutoResolveParams);
        var conditionsChanged = rule.IsEnabled != request.IsEnabled
            || rule.ConditionType != request.ConditionType
            || !sameBody
            || rule.AutoResolveEnabled != request.AutoResolveEnabled
            || !sameAutoResolve;
        var snoozeChanged = !SmartSnoozeConfig.SameConditions(
            SmartSnoozeConfig.StoredConditions(rule.ClientConfiguration),
            SmartSnoozeConfig.StoredConditions(trees.ClientConfiguration));

        rule.Name = request.Name;
        rule.Description = request.Description;
        rule.ConditionType = request.ConditionType;
        if (!sameBody)
            rule.ConditionParams = conditionParamsJson;
        rule.ScopeClass = _scopeClassifier.Classify(request.ConditionType, conditionParamsJson);
        var wasEnabled = rule.IsEnabled;
        rule.IsEnabled = request.IsEnabled;
        rule.SortOrder = request.SortOrder;
        rule.Severity = request.Severity ?? AlertRuleSeverity.Warning;
        rule.AllowThroughDnd = request.AllowThroughDnd;
        rule.AutoResolveEnabled = request.AutoResolveEnabled;
        if (!sameAutoResolve)
            rule.AutoResolveParams = trees.AutoResolveParams;
        rule.ClientConfiguration = trees.ClientConfiguration ?? "{}";
        rule.ReviewReason = null;
        rule.UpdatedAt = DateTime.UtcNow;

        if (request.Channels is not null)
        {
            var retainedSecrets = AlertRuleChannelWriter.CollectRetainedSecrets(rule.Channels);

            // Replace the channel list wholesale. Cascade-delete on AlertRuleChannelEntity ⇒
            // AlertDeliveryEntity is configured as SetNull (not Cascade) to preserve the audit
            // trail of historical deliveries even when the source channel is reconfigured.
            db.AlertRuleChannels.RemoveRange(rule.Channels);
            rule.Channels.Clear();

            var sortIndex = 0;
            foreach (var ch in request.Channels)
            {
                rule.Channels.Add(_channels.Build(ch, rule.Id, tenantId, sortIndex++, retainedSecrets));
            }
        }

        // A save without its clear would stand: a retried edit changes nothing, so it would not
        // clear the hold.
        if (conditionsChanged)
            await _rearm.SaveAndClearAsync(unit, id, ct);
        else if (snoozeChanged)
            await _rearm.SaveAndPruneAsync(unit, id, ct);
        else
            await db.SaveChangesAsync(ct);
        if (wasEnabled && !request.IsEnabled)
            await _retirement.CloseAsync([id], tenantId, CancellationToken.None);

        return Ok(await ReadBackAsync(tenantId, id, ct));
    }

    /// <summary>
    /// Delete an alert rule (cascades to its channels).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetRules"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ReferencingRulesResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
            return NotFound();

        // Managed rules are owned by their source feature's configuration (e.g. a tracker
        // notification threshold) — deleting here would only get re-synthesised by the
        // backfill. Delete the source config instead.
        if (rule.ManagedBy is not null)
        {
            return Conflict(new ReferencingRulesResponse([], rule.ManagedBy));
        }

        // Refuse to break the alert_state graph: if any other rule references this one, the
        // caller must update or delete those first. Returning the offending ids lets the FE
        // either link to them or offer a cascade-delete.
        var referencing = await _referenceService.FindReferencingRulesAsync(id, ct);
        if (referencing.Count > 0)
        {
            return Conflict(new ReferencingRulesResponse(referencing));
        }

        // AlertRuleRetirement's remarks: a delete closes first. An abort here leaves the rule in
        // place with its excursion closed.
        if (rule.IsEnabled)
            await _retirement.CloseAsync([id], db.TenantId, ct);

        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Toggle an alert rule enabled/disabled.
    /// </summary>
    [HttpPatch("{id:guid}/toggle")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetRules", "GetRule"])]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> ToggleRule(Guid id, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contextFactory, ct);
        var db = unit.Context;

        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
            return NotFound();

        var wasEnabled = rule.IsEnabled;
        rule.IsEnabled = !rule.IsEnabled;
        rule.UpdatedAt = DateTime.UtcNow;
        await _rearm.SaveAndClearAsync(unit, id, ct);
        if (wasEnabled)
            await _retirement.CloseAsync([id], db.TenantId, CancellationToken.None);

        return Ok(await ReadBackAsync(db.TenantId, id, ct));
    }

    /// <summary>
    /// Fire a saved rule through its real channel list as a test. Writes a
    /// <c>is_test=true</c> instance + delivery rows so the user can verify their channels
    /// without polluting the active-alerts surface.
    /// </summary>
    [HttpPost("{id:guid}/test-fire")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestFire(Guid id, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var rule = await _alerts.GetRuleAsync(tenantId, id, ct);
        if (rule is null) return NotFound();

        await _deliveryService.TestFireAsync(id, rule.Channels, BuildTestPayload(rule.Rule, tenantId), ct);
        return Accepted();
    }

    /// <summary>
    /// Test-fire variant for the editor on an unsaved rule. Same provider chain, no
    /// rule lookup — channels and metadata come straight from the request body.
    /// </summary>
    [HttpPost("test-fire-dry-run")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestFireDryRun(
        [FromBody] TestFireDryRunRequest request, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        // Held to the same rules as a save: a preview that accepted a destination the editor
        // would refuse to store would report success for a channel that cannot deliver. The
        // request names no rule, so any in-app recipient stored on this site counts as stored.
        var storedRecipients = (await db.AlertRuleChannels
                .AsNoTracking()
                .Where(c => c.ChannelType == ChannelType.InApp)
                .Select(c => c.Destination)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        if (await ResolveAndValidateChannelsAsync(request.Channels, storedRecipients, db, ct) is { } badChannel)
            return badChannel;

        var tenantId = db.TenantId;

        // Synthesise channel snapshots with provisional ids — none of them point to a
        // saved row, but the delivery service treats them as opaque AlertRuleChannelId
        // values which become AlertDeliveryEntity.AlertRuleChannelId=null on persistence
        // (the FK is SetNull). This is fine because dry-run rules don't have saved
        // channels to back-reference.
        // The snapshot's secret is ciphertext everywhere else, so the preview's plaintext is
        // encrypted here rather than the provider learning a second input shape.
        var channels = request.Channels
            .Select((c, i) => new AlertRuleChannelSnapshot(
                Guid.Empty, Guid.Empty, c.ChannelType, c.Destination ?? string.Empty,
                c.DestinationLabel, i, AlertRuleChannelWriter.SerializeMetadata(c.Metadata), _channels.EncryptSecret(c.Secret)))
            .ToList();

        var payload = new AlertPayload
        {
            AlertType = AlertConditionType.Composite,
            RuleName = $"[Test] {request.Name}",
            Severity = request.Severity,
            TenantId = tenantId,
            GlucoseValue = null,
            Trend = null,
            TrendRate = null,
            ReadingTimestamp = DateTime.UtcNow,
            SubjectName = "Test fire",
            ExcursionId = Guid.Empty,
            InstanceId = Guid.Empty,
            ActiveExcursionCount = 0,
        };

        await _deliveryService.TestFireDryRunAsync(channels, payload, ct);
        return Accepted();
    }

    internal static AlertPayload BuildTestPayload(AlertRuleSnapshot rule, Guid tenantId) => new()
    {
        AlertType = rule.ConditionType,
        RuleName = $"[Test] {rule.Name}",
        Severity = rule.Severity,
        TenantId = tenantId,
        GlucoseValue = null,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Test fire",
        ExcursionId = Guid.Empty,
        InstanceId = Guid.Empty,
        ActiveExcursionCount = 0,
    };

    #region Helpers

    /// <inheritdoc cref="AlertRuleChannelWriter.ResolveAndValidateAsync"/>
    /// <returns>A 400 <see cref="BadRequestObjectResult"/> on the first offender, or null when all are valid.</returns>
    private async Task<ActionResult?> ResolveAndValidateChannelsAsync(
        List<CreateAlertRuleChannelRequest>? channels,
        IReadOnlySet<string> storedRecipients,
        NocturneDbContext db,
        CancellationToken ct) =>
        await _channels.ResolveAndValidateAsync(channels, storedRecipients, db, HttpContext?.GetSubjectId(), ct) is { } message
            ? BadRequest(new { message })
            : null;

    private async Task<AlertRuleResponse> ToResponseAsync(AlertRuleRecord rule, CancellationToken ct) =>
        (await ToResponsesAsync([rule], ct))[0];

    /// <summary>The response for a rule this request has just committed.</summary>
    private async Task<AlertRuleResponse> ReadBackAsync(Guid tenantId, Guid ruleId, CancellationToken ct) =>
        await ToResponseAsync(
            await _alerts.GetRuleAsync(tenantId, ruleId, ct)
                ?? throw new InvalidOperationException($"Alert rule {ruleId} was not found after it was saved."),
            ct);

    /// <summary>
    /// What a save of the stored rule would reject. A rule the engine cannot take at all is logged
    /// and listed without issues, so one such rule does not fail the whole list.
    /// </summary>
    private IReadOnlyList<RustValidationIssue> StoredIssues(AlertRuleSnapshot rule)
    {
        try
        {
            return _conditionValidator.Validate(
                rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled,
                rule.AutoResolveParams, rule.ClientConfiguration);
        }
        catch (RustAlertEngineException ex)
        {
            _logger.LogError(ex, "The alert engine could not check the conditions of alert rule {AlertRuleId}", rule.Id);
            return [];
        }
    }

    /// <summary>
    /// Maps rules to responses and, for a caller who may edit the rules, describes each in-app
    /// channel's recipient. Only the rule editor reads it, so other callers are spared the lookup.
    /// This is not a privacy boundary: member names are readable more widely (issue #2359).
    /// </summary>
    private async Task<List<AlertRuleResponse>> ToResponsesAsync(
        IEnumerable<AlertRuleRecord> rules, CancellationToken ct)
    {
        var responses = rules.Select(rule => MapToResponse(rule, StoredIssues(rule.Rule))).ToList();
        var inApp = responses
            .SelectMany(r => r.Channels)
            .Where(c => c.ChannelType == ChannelType.InApp)
            .ToList();
        if (inApp.Count == 0 || HttpContext?.HasScope(Scope.AlertsReadWrite) != true)
        {
            return responses;
        }

        await using var db = await _contextFactory.CreateAsync(ct);
        var recipients = await InAppRecipients.ListAsync(db, db.TenantId, ct);
        foreach (var channel in inApp)
        {
            Guid? subjectId = Guid.TryParse(channel.Destination, out var parsed) ? parsed : null;
            string? name = null;
            var canReceive = subjectId is { } id && recipients.TryGetValue(id, out name);
            channel.Recipient = new InAppRecipientResponse
            {
                SubjectId = subjectId,
                Name = name,
                CanReceive = canReceive,
            };
        }

        return responses;
    }

    /// <summary>
    /// The members an in-app channel may be sent to, for the rule editor's recipient picker.
    /// </summary>
    [HttpGet("in-app-recipients")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<InAppRecipientResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<InAppRecipientResponse>>> GetInAppRecipients(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var members = await InAppRecipients.ListAsync(db, db.TenantId, ct);
        return Ok(members
            .Select(m => new InAppRecipientResponse { SubjectId = m.Key, Name = m.Value, CanReceive = true })
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    private static AlertRuleResponse MapToResponse(
        AlertRuleRecord record, IReadOnlyList<RustValidationIssue> conditionIssues) => new()
    {
        Id = record.Rule.Id,
        Name = record.Rule.Name,
        Description = record.Description,
        ConditionType = record.Rule.ConditionType,
        ConditionParams = DeserializeJson(record.Rule.ConditionParams),
        IsEnabled = record.IsEnabled,
        SortOrder = record.Rule.SortOrder,
        Severity = record.Rule.Severity,
        AllowThroughDnd = record.Rule.AllowThroughDnd,
        ScopeClass = record.Rule.ScopeClass,
        ManagedBy = record.ManagedBy,
        ReviewReason = record.ReviewReason,
        ConditionIssues = [.. conditionIssues],
        AutoResolveEnabled = record.Rule.AutoResolveEnabled,
        AutoResolveParams = record.Rule.AutoResolveParams is null
            ? null
            : DeserializeJson(record.Rule.AutoResolveParams),
        ClientConfiguration = DeserializeJson(record.Rule.ClientConfiguration),
        Channels = record.Channels
            .Select(c => new AlertRuleChannelResponse
            {
                Id = c.Id,
                ChannelType = c.ChannelType,
                Destination = c.Destination,
                DestinationLabel = c.DestinationLabel,
                SortOrder = c.SortOrder,
                Metadata = c.Metadata is null ? null : DeserializeJson(c.Metadata),
                HasSecret = c.Secret is not null,
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
    /// Returns a <c>400 BadRequest</c> when the rule contains a <c>tracker_age</c> leaf whose
    /// <c>tracker_definition_id</c> is missing, malformed, or does not exist for this tenant.
    /// Without this the rule saves fine but the engine fails closed on every reading and sweep
    /// pass, a rule that silently never fires. Returns null when the request is acceptable.
    /// </summary>
    private static async Task<BadRequestObjectResult?> RejectInvalidTrackerAgeAsync(
        NocturneDbContext db, IReadOnlyList<ConditionLeaf> leaves, CancellationToken ct)
    {
        var definitionIds = new List<Guid>();
        foreach (var leaf in leaves.Where(leaf => leaf.Kind == AlertConditionType.TrackerAge))
        {
            if (leaf.GuidParam("tracker_definition_id") is not { } definitionId || definitionId == Guid.Empty)
                return new BadRequestObjectResult("tracker_age requires a tracker_definition_id.");
            definitionIds.Add(definitionId);
        }

        foreach (var definitionId in definitionIds)
        {
            if (!await db.TrackerDefinitions.AnyAsync(d => d.Id == definitionId, ct))
                return new BadRequestObjectResult($"Unknown tracker definition '{definitionId}'.");
        }

        return null;
    }

    /// <summary>
    /// Returns a <c>400</c> validation problem when a condition tree the rule evaluates has a
    /// problem (docs/alerts/engine-semantics.md §1.4). Each <c>errors</c> key is
    /// <c>{scope}:{path}</c> and each value a reason code, suffixed <c>:{field}</c> when the
    /// problem is on a field; the <c>issues</c> extension carries the same list structured.
    /// </summary>
    private ActionResult? RejectInvalidConditions(AlertConditionType type, RequestTrees trees, bool autoResolveEnabled) =>
        ConditionProblem(_conditionValidator.Validate(
            type, trees.ConditionParams, autoResolveEnabled, trees.AutoResolveParams, trees.ClientConfiguration));

    /// <inheritdoc cref="RejectInvalidConditions"/>
    private ActionResult? ConditionProblem(IReadOnlyList<RustValidationIssue> issues)
    {
        if (issues.Count == 0)
            return null;

        var errors = issues
            .GroupBy(i => $"{i.Scope}:{i.Path}")
            .ToDictionary(
                g => g.Key,
                g => g.Select(i => i.Field is null ? i.Reason : $"{i.Reason}:{i.Field}").ToArray());
        var problem = new ValidationProblemDetails(errors)
        {
            Title = "The rule's conditions cannot be saved.",
            Status = StatusCodes.Status400BadRequest,
        };
        problem.Extensions["issues"] = issues;
        return ValidationProblem(problem);
    }

    /// <summary>
    /// A request's condition trees serialised as they are stored. A null tree stays null, except the
    /// body, stored as <c>{}</c>.
    /// </summary>
    private sealed record RequestTrees(string ConditionParams, string? AutoResolveParams, string? ClientConfiguration)
    {
        public static RequestTrees From(object? conditionParams, object? autoResolveParams, object? clientConfiguration) =>
            new(
                conditionParams is not null ? JsonSerializer.Serialize(conditionParams) : "{}",
                autoResolveParams is not null ? JsonSerializer.Serialize(autoResolveParams) : null,
                clientConfiguration is not null ? JsonSerializer.Serialize(clientConfiguration) : null);
    }

    #endregion
}

/// <summary>
/// 409 response body returned by <c>DELETE /api/v4/alert-rules/{id}</c>. Either other rules
/// reference the target via <c>alert_state</c> (<see cref="ReferencingRuleIds"/> is non-empty,
/// and the FE can link to them or offer a cascade-delete confirmation), or the rule is owned
/// by a source feature (<see cref="ManagedBy"/> is non-null) and must be deleted there.
/// </summary>
/// <remarks>
/// <see cref="Status"/> and <see cref="Message"/> are carried in the body because the generated
/// client reads both off the thrown value; a body declaring neither is flattened to a 500.
/// One record covers both branches because an operation declares a single schema per status.
/// </remarks>
public record ReferencingRulesResponse(IReadOnlyList<Guid> ReferencingRuleIds, string? ManagedBy = null)
{
    /// <summary>The status this body is returned with.</summary>
    public int Status => StatusCodes.Status409Conflict;

    /// <summary>The reason, worded for the person who asked for the deletion.</summary>
    public string Message =>
        ManagedBy is not null
            ? $"This rule is managed by '{ManagedBy}' — delete the tracker notification threshold instead."
            : ReferencingRuleIds.Count <= 1
                ? "Another alert rule's condition refers to this one. Update that rule first."
                : $"{ReferencingRuleIds.Count} other alert rules' conditions refer to this one. Update those rules first.";
}

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
    /// <summary>When true, this rule still fires while the tenant is in Do not disturb mode.
    /// Critical rules implicitly bypass DND regardless of this flag.</summary>
    public bool AllowThroughDnd { get; set; }
    /// <summary>Low/high classification for scoped Do not disturb (ADR 0004), derived by the
    /// shared engine from the rule's directional leaves. Read-only — computed server-side on
    /// create/update; a scoped <c>lows</c>/<c>highs</c> window silences a rule only when its
    /// class matches.</summary>
    public RuleScopeClass ScopeClass { get; set; } = RuleScopeClass.Undirected;
    /// <summary>Owner tag when this rule is synthesised from another feature's configuration
    /// (e.g. <c>tracker:{definitionId}</c>). Null for user-authored rules. Managed rules
    /// cannot be deleted here — the owning configuration re-syncs their condition, name and
    /// severity; channels and client configuration remain user-editable.</summary>
    public string? ManagedBy { get; set; }
    /// <summary>Why a data repair changed or disabled this rule (<see cref="AlertRuleReviewReasons"/>);
    /// null when none did. Cleared by the next edit.</summary>
    public string? ReviewReason { get; set; }
    /// <summary>Every problem that keeps one of the rule's stored condition trees from being
    /// evaluated: with one in its body the rule is skipped, in its auto-resolve tree it never
    /// resolves, and in its smart-snooze conditions the snooze clears.</summary>
    public List<RustValidationIssue> ConditionIssues { get; set; } = [];
    public bool AutoResolveEnabled { get; set; }
    public object? AutoResolveParams { get; set; }
    public object ClientConfiguration { get; set; } = new { };
    /// <summary>Flat list of delivery channels. Dispatched in parallel when the rule fires.</summary>
    public List<AlertRuleChannelResponse> Channels { get; set; } = [];
}

public class AlertRuleChannelResponse
{
    public Guid Id { get; set; }
    public ChannelType ChannelType { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string? DestinationLabel { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Channel-specific config (e.g. device_action capabilities). Null when unset.</summary>
    public object? Metadata { get; set; }
    /// <summary>Whether a webhook signing secret is stored for this channel. The secret itself is
    /// never returned.</summary>
    public bool HasSecret { get; set; }
    /// <summary>Who an <c>in_app</c> channel is sent to. Set only for in-app channels, and only
    /// for a caller who may edit alert rules.</summary>
    public InAppRecipientResponse? Recipient { get; set; }
}

/// <summary>The recipient of an <c>in_app</c> alert channel.</summary>
public class InAppRecipientResponse
{
    /// <summary>The subject the channel names. Null when it names none.</summary>
    public Guid? SubjectId { get; set; }
    /// <summary>The name the site knows the member by. Null when they cannot receive.</summary>
    public string? Name { get; set; }
    /// <summary>Whether the channel reaches someone: the subject is a member of the site whose
    /// roles grant any access. A channel that does not is sent to whoever next saves the
    /// rule.</summary>
    public bool CanReceive { get; set; }
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
    public bool AllowThroughDnd { get; set; }
    public bool AutoResolveEnabled { get; set; }
    public object? AutoResolveParams { get; set; }
    public object? ClientConfiguration { get; set; }
    public List<CreateAlertRuleChannelRequest>? Channels { get; set; }
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
    public bool AllowThroughDnd { get; set; }
    public bool AutoResolveEnabled { get; set; }
    public object? AutoResolveParams { get; set; }
    public object? ClientConfiguration { get; set; }
    public List<CreateAlertRuleChannelRequest>? Channels { get; set; }
}

public class CreateAlertRuleChannelRequest
{
    public ChannelType ChannelType { get; set; }
    /// <summary>Channel-specific address: webhook URL, chat handle, device kind for device_action, etc. Empty for in-app/web-push.</summary>
    public string? Destination { get; set; }
    public string? DestinationLabel { get; set; }
    /// <summary>Channel-specific config, persisted as JSONB. For device_action: <c>{ "capabilities": ["notify", ...] }</c>.</summary>
    public object? Metadata { get; set; }
    /// <summary>
    /// Write-only HMAC signing secret for a <c>webhook</c> channel; the receiver verifies it
    /// against the <c>X-Nocturne-Signature</c> header. Omit to keep the secret stored against this
    /// channel type and destination — changing either is a new channel and carries no secret over.
    /// Send empty to clear it. At most 256 bytes once UTF-8 encoded. Never returned — the read side
    /// reports <see cref="AlertRuleChannelResponse.HasSecret"/> instead.
    /// </summary>
    [MaxLength(256)]
    public string? Secret { get; set; }
}

/// <summary>
/// Request body for the dry-run test fire endpoint. Mirrors the editor's in-memory rule
/// shape — only the fields needed to render a notification.
/// </summary>
public record TestFireDryRunRequest(
    string Name,
    AlertRuleSeverity Severity,
    List<CreateAlertRuleChannelRequest> Channels);

#endregion
