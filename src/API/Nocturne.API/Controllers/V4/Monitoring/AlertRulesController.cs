using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Authorization;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Alert rules for the tenant. Each rule sets the conditions that trigger an alert and the delivery
/// channels that receive it. Create, read, update, delete, turn on or off, and test-fire rules.
/// </summary>
/// <remarks>
/// <para>
/// Each rule owns a flat list of delivery channels. Time-of-day gating, quiet-hours, and escalation
/// chains are expressed inside the rule's condition tree (<c>time_of_day</c>, <c>do_not_disturb</c>,
/// <c>alert_state</c>) rather than as side-channel schedule/step structures.
/// </para>
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
    private readonly ITenantMembershipRepository _members;
    private readonly ISubjectRepository _subjects;
    private readonly IAlertRepository _alerts;
    private readonly IAlertRuleRepository _rules;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAlertReferenceService _referenceService;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly IRuleScopeClassifier _scopeClassifier;
    private readonly IAlertRuleConditionValidator _conditionValidator;
    private readonly AlertRuleChannelWriter _channels;
    private readonly AlertRuleRearm _rearm;
    private readonly AlertRuleRetirement _retirement;
    private readonly ILogger<AlertRulesController> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertRulesController"/>.
    /// </summary>
    public AlertRulesController(
        INocturneDbContextFactory contextFactory,
        ITenantMembershipRepository members,
        ISubjectRepository subjects,
        IAlertRepository alerts,
        IAlertRuleRepository rules,
        ITenantAccessor tenantAccessor,
        IAlertReferenceService referenceService,
        IAlertDeliveryService deliveryService,
        IRuleScopeClassifier scopeClassifier,
        IAlertRuleConditionValidator conditionValidator,
        ISecretEncryptionService encryption,
        AlertRuleRearm rearm,
        AlertRuleRetirement retirement,
        ILogger<AlertRulesController> logger,
        TimeProvider timeProvider)
    {
        _time = timeProvider;
        _contextFactory = contextFactory;
        _members = members;
        _subjects = subjects;
        _alerts = alerts;
        _rules = rules;
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
        var rules = await _alerts.GetRulesAsync(ct);
        return Ok(await ToResponsesAsync(rules, ct));
    }

    /// <summary>
    /// The ready-made alerts, in the order they are offered: what the alert wizard lists and what
    /// the setup hub starts a tenant with.
    /// </summary>
    [HttpGet("presets")]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AlertPresetResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<AlertPresetResponse>> GetPresets() =>
        Ok(AlertPresets.All
            .Select(p => new AlertPresetResponse
            {
                Id = p.Id,
                Group = p.Group,
                Severity = p.Severity,
                CanWait = p.CanWait,
                ConditionType = p.ConditionType,
                ConditionParams = p.ConditionParams is null ? null : AlertRuleResponses.Json(p.ConditionParams),
                AutoResolve = p.AutoResolve,
                TrackerCategories = [.. p.TrackerCategories],
                TrackerOnlyAgeHours = p.TrackerOnlyAgeHours,
                StarterKind = p.StarterKind,
            })
            .ToList());

    /// <summary>
    /// The values a rule's missing-data behaviour and glucose freshness bound may take.
    /// </summary>
    [HttpGet("setting-options")]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ErrorEnvelope]
    [ProducesResponseType(typeof(AlertRuleSettingOptionsResponse), StatusCodes.Status200OK)]
    public ActionResult<AlertRuleSettingOptionsResponse> GetSettingOptions() =>
        Ok(new AlertRuleSettingOptionsResponse
        {
            OnUnknownValues = [.. AlertRuleOnUnknown.All],
            DefaultOnUnknown = AlertRuleOnUnknown.Hold,
            DefaultGlucoseFreshnessMinutes = AlertRuleGlucoseFreshness.DefaultMinutes,
            MinGlucoseFreshnessMinutes = AlertRuleGlucoseFreshness.MinMinutes,
            MaxGlucoseFreshnessMinutes = AlertRuleGlucoseFreshness.MaxMinutes,
        });

    /// <summary>
    /// The condition that closes an alert at once: the opposite of the given condition tree. The
    /// answer has no condition when the tree has no opposite.
    /// </summary>
    [HttpPost("auto-resolve-suggestion")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(typeof(AutoResolveSuggestionResponse), StatusCodes.Status200OK)]
    public ActionResult<AutoResolveSuggestionResponse> SuggestAutoResolve([FromBody] AutoResolveSuggestionRequest request)
    {
        var suggestion = request.Condition is null
            ? null
            : AutoResolveSuggestion.For(JsonSerializer.Serialize(request.Condition));
        return Ok(new AutoResolveSuggestionResponse
        {
            Condition = suggestion is null ? null : AlertRuleResponses.Json(suggestion),
        });
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
        var rule = await _alerts.GetRuleAsync(id, ct);
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
        await using var unit = await DbUnitOfWork.OpenAsync(_contextFactory, ct);
        var db = unit.Context;

        if (await ResolveAndValidateChannelsAsync(request.Channels, AlertRuleChannelWriter.NoStoredRecipients, db, ct) is { } badChannel)
            return badChannel;

        var leaves = RuleTrees.Leaves(request.ConditionType, trees.ConditionParams);
        if (await RejectInvalidTrackerAgeAsync(new TrackerRepository(_contextFactory), leaves, ct) is { } badTracker)
            return badTracker;

        var conditionParamsJson = trees.ConditionParams;
        var channels = await ToWritesAsync(request.Channels ?? [], db, ct);

        var ruleId = await new AlertRuleRepository(unit, _time).CreateAsync(
            new NewAlertRule(
                new AlertRuleWrite(
                    request.Name,
                    request.Description,
                    request.ConditionType,
                    conditionParamsJson,
                    _scopeClassifier.Classify(request.ConditionType, conditionParamsJson),
                    request.Severity ?? AlertRuleSeverity.Warning,
                    request.IsEnabled,
                    request.SortOrder,
                    request.AllowThroughDnd,
                    request.AutoResolveEnabled,
                    trees.AutoResolveParams,
                    trees.ClientConfiguration ?? "{}",
                    OnUnknown: request.OnUnknown ?? AlertRuleOnUnknown.Hold,
                    GlucoseFreshnessMinutes: request.GlucoseFreshnessMinutes),
                channels),
            ct);
        await _rearm.SaveAndClearAsync(unit, ruleId, ct);

        return CreatedAtAction(nameof(GetRule), new { id = ruleId }, await ReadBackAsync(ruleId, ct));
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
        var tenantId = _tenantAccessor.TenantId;
        var rule = await _alerts.GetRuleAsync(id, ct);
        if (rule is null)
            return NotFound();

        var trees = RequestTrees.From(request.ConditionParams, request.AutoResolveParams, request.ClientConfiguration);
        if (RejectInvalidConditions(request.ConditionType, trees, request.AutoResolveEnabled) is { } invalid)
            return invalid;

        await using var unit = await DbUnitOfWork.OpenAsync(_contextFactory, ct);
        var db = unit.Context;

        if (await ResolveAndValidateChannelsAsync(request.Channels, AlertRuleChannelWriter.InAppRecipientsOf(rule.Channels), db, ct) is { } badChannel)
            return badChannel;

        var leaves = RuleTrees.Leaves(request.ConditionType, trees.ConditionParams);
        if (await RejectInvalidTrackerAgeAsync(new TrackerRepository(unit), leaves, ct) is { } badTracker)
            return badTracker;

        if (await _referenceService.DetectCycleAsync(id, RuleTrees.AlertStateTargets(leaves).ToList(), ct))
        {
            return BadRequest("Cyclical alert_state reference detected.");
        }

        var conditionParamsJson = trees.ConditionParams;

        var sameBody = ConditionTreeEquality.Same(rule.Rule.ConditionParams, conditionParamsJson);
        var sameAutoResolve = ConditionTreeEquality.Same(rule.Rule.AutoResolveParams, trees.AutoResolveParams);
        var onUnknown = request.OnUnknown ?? rule.Rule.OnUnknown;
        var glucoseFreshnessMinutes = request.ClearGlucoseFreshness
            ? null
            : request.GlucoseFreshnessMinutes ?? rule.Rule.GlucoseFreshnessMinutes;
        // A rule shown with a stand-in is not evaluated, so the save that stores a member makes it
        // evaluable whatever it changes.
        var conditionsChanged = rule.Unreadable != AlertRuleUnreadableValues.None
            || rule.IsEnabled != request.IsEnabled
            || rule.Rule.ConditionType != request.ConditionType
            || !sameBody
            || rule.Rule.GlucoseFreshnessMinutes != glucoseFreshnessMinutes
            || !string.Equals(rule.Rule.OnUnknown, onUnknown, StringComparison.Ordinal)
            || rule.Rule.AutoResolveEnabled != request.AutoResolveEnabled
            || !sameAutoResolve;
        var snoozeChanged = !SmartSnoozeConfig.SameConditions(
            SmartSnoozeConfig.StoredConditions(rule.Rule.ClientConfiguration),
            SmartSnoozeConfig.StoredConditions(trees.ClientConfiguration));
        var wasEnabled = rule.IsEnabled;

        var rules = new AlertRuleRepository(unit, _time);
        var write = new AlertRuleWrite(
            request.Name,
            request.Description,
            request.ConditionType,
            sameBody ? rule.Rule.ConditionParams : conditionParamsJson,
            _scopeClassifier.Classify(request.ConditionType, conditionParamsJson),
            request.Severity ?? AlertRuleSeverity.Warning,
            request.IsEnabled,
            request.SortOrder,
            request.AllowThroughDnd,
            request.AutoResolveEnabled,
            sameAutoResolve ? rule.Rule.AutoResolveParams : trees.AutoResolveParams,
            trees.ClientConfiguration ?? "{}",
            OnUnknown: onUnknown,
            GlucoseFreshnessMinutes: glucoseFreshnessMinutes);
        if (!await rules.UpdateAsync(id, write, ct))
            return NotFound();
        if (request.Channels is not null)
            await rules.ReplaceChannelsAsync(id, await ToWritesAsync(request.Channels, db, ct), kept: null, ct);

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

        return Ok(await ReadBackAsync(id, ct));
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
        var tenantId = _tenantAccessor.TenantId;
        var rule = await _alerts.GetRuleAsync(id, ct);
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
            await _retirement.CloseAsync([id], tenantId, ct);

        if (!await _rules.DeleteAsync(id, ct))
            return NotFound();

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

        if (await new AlertRuleRepository(unit, _time).ToggleEnabledAsync(id, ct) is not { } wasEnabled)
            return NotFound();

        await _rearm.SaveAndClearAsync(unit, id, ct);
        if (wasEnabled)
            await _retirement.CloseAsync([id], db.TenantId, CancellationToken.None);

        return Ok(await ReadBackAsync(id, ct));
    }

    /// <summary>
    /// Keep a channel that stops when the access of the member who added it ends: the caller
    /// becomes the member who added it, so it stops when the caller's own access ends, or never
    /// when the caller's access has no end. Only a channel whose destination was typed in can be
    /// kept; a channel that goes to a member sends again once that member has access.
    /// </summary>
    [HttpPost("{ruleId:guid}/channels/{channelId:guid}/keep")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetRules", "GetRule"])]
    [ErrorEnvelope]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> KeepChannel(Guid ruleId, Guid channelId, CancellationToken ct)
    {
        var channel = (await _alerts.GetRuleAsync(ruleId, ct))?.Channels.FirstOrDefault(c => c.Id == channelId);
        if (channel is null)
            return NotFound();

        if (ChannelDestinations.ModeOf(channel.ChannelType) != ChannelDestinationMode.UserSupplied)
            return Problem(detail: KeepMemberChannelDetail, statusCode: StatusCodes.Status400BadRequest);

        if (HttpContext?.GetSubjectId() is not { } subjectId)
            return Problem(detail: KeepWithoutMemberDetail, statusCode: StatusCodes.Status400BadRequest);

        if (!await _rules.RestampChannelAsync(ruleId, channelId, subjectId, HttpContext.CallerAccessEndsAt(), ct))
            return NotFound();

        return Ok(await ReadBackAsync(ruleId, ct));
    }

    private const string KeepMemberChannelDetail =
        "Only a channel with a typed-in destination can be kept. A channel to a member sends again when that member has access.";

    private const string KeepWithoutMemberDetail = "Only a member can keep a channel.";

    /// <summary>
    /// Fire a saved rule through its real channel list as a test. Writes a
    /// <c>is_test=true</c> instance + delivery rows so the user can verify their channels
    /// without polluting the active-alerts surface. The answer lists the channels it did not send
    /// because they do not deliver, with their state, by position in the rule's channels in
    /// <c>sortOrder</c> order.
    /// </summary>
    [HttpPost("{id:guid}/test-fire")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(typeof(TestFireResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestFire(Guid id, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var rule = await _alerts.GetRuleAsync(id, ct);
        if (rule is null) return NotFound();

        var fired = await _deliveryService.TestFireAsync(id, rule.Channels, AlertPayloads.Test(rule.Rule.ConditionType, rule.Rule.Name, rule.Rule.Severity, tenantId, _time.GetUtcNow().UtcDateTime), ct);
        return Accepted(new TestFireResponse { NotSent = [.. fired.NotSent] });
    }

    /// <summary>
    /// Test-fire variant for the editor on an unsaved rule. Same provider chain, no
    /// rule lookup — channels and metadata come straight from the request body. The answer lists
    /// the channels it did not send because they do not deliver, with their state.
    /// </summary>
    [HttpPost("test-fire-dry-run")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(typeof(TestFireResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestFireDryRun(
        [FromBody] TestFireDryRunRequest request, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        // Held to the same rules as a save: a preview that accepted a destination the editor
        // would refuse to store would report success for a channel that cannot deliver. The
        // request names no rule, so any in-app recipient stored on this site counts as stored.
        var storedRecipients = await _rules.GetInAppDestinationsAsync(ct);
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

        var payload = AlertPayloads.Test(AlertConditionType.Composite, request.Name, request.Severity, tenantId, _time.GetUtcNow().UtcDateTime);

        var notSent = await _deliveryService.TestFireDryRunAsync(channels, payload, ct);
        return Accepted(new TestFireResponse { NotSent = [.. notSent ?? []] });
    }

    #region Helpers

    /// <inheritdoc cref="AlertRuleChannelWriter.ResolveAndValidateAsync"/>
    /// <returns>A 400 <see cref="BadRequestObjectResult"/> on the first offender, or null when all are valid.</returns>
    private async Task<ActionResult?> ResolveAndValidateChannelsAsync(
        List<CreateAlertRuleChannelRequest>? channels,
        IReadOnlySet<string> storedRecipients,
        NocturneDbContext db,
        CancellationToken ct) =>
        await _channels.ResolveAndValidateAsync(
            channels, storedRecipients, db, _members, HttpContext?.GetSubjectId(), _time.GetUtcNow().UtcDateTime, ct) is { } message
            ? BadRequest(new { message })
            : null;

    /// <inheritdoc cref="AlertRuleChannelWriter.ToWritesAsync"/>
    private Task<List<AlertRuleChannelWrite>> ToWritesAsync(
        IEnumerable<CreateAlertRuleChannelRequest> channels, NocturneDbContext db, CancellationToken ct) =>
        _channels.ToWritesAsync(channels, HttpContext?.GetSubjectId(), HttpContext?.CallerAccessEndsAt(), db, ct);

    private async Task<AlertRuleResponse> ToResponseAsync(AlertRuleRecord rule, CancellationToken ct) =>
        (await ToResponsesAsync([rule], ct, await _alerts.GetRulesAsync(ct)))[0];

    /// <summary>The response for a rule this request has just committed.</summary>
    private async Task<AlertRuleResponse> ReadBackAsync(Guid ruleId, CancellationToken ct) =>
        await ToResponseAsync(
            await _alerts.GetRuleAsync(ruleId, ct)
                ?? throw new InvalidOperationException($"Alert rule {ruleId} was not found after it was saved."),
            ct);

    /// <summary>
    /// What a save of the stored rule would reject. A rule the engine cannot take at all is logged
    /// and listed without issues, so one such rule does not fail the whole list. The result is null
    /// then, which tells a rule that could not be checked from one with no issues.
    /// </summary>
    private IReadOnlyList<RustValidationIssue>? StoredIssues(AlertRuleSnapshot rule)
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
            return null;
        }
    }

    /// <summary>
    /// Maps rules to responses and, for a caller who may edit the rules, describes each in-app
    /// channel's recipient and each channel's state with the members it names. Only the rule editor
    /// reads it, so other callers are spared the lookup. This is not a privacy boundary: member
    /// names are readable more widely (issue #2359).
    /// </summary>
    private async Task<List<AlertRuleResponse>> ToResponsesAsync(
        IReadOnlyList<AlertRuleRecord> rules, CancellationToken ct,
        IReadOnlyList<AlertRuleRecord>? tenantRules = null)
    {
        var tenantId = _tenantAccessor.TenantId;
        var now = _time.GetUtcNow().UtcDateTime;
        var allRules = tenantRules ?? rules;
        var issues = allRules.ToDictionary(r => r.Rule.Id, r => StoredIssues(r.Rule));
        var states = await AlertChannelStates.LoadAsync(
            allRules.Concat(rules).SelectMany(r => r.Channels), tenantId, now, _members, _contextFactory, ct);
        var covering = LowCoverage.CoveringRuleIds(allRules.Select(r => (r, issues[r.Rule.Id])), states);
        var holds = (await _alerts.GetHeldRuleStatesAsync(ct)).ToDictionary(h => h.AlertRuleId, AlertHold.Of);
        var responses = rules
            .Select(r => MapToResponse(r, issues.TryGetValue(r.Rule.Id, out var found) ? found : StoredIssues(r.Rule), covering))
            .ToList();
        foreach (var response in responses.Where(r => r.IsEnabled))
            response.Hold = holds.GetValueOrDefault(response.Id);
        if (HttpContext?.HasScope(Scope.AlertsReadWrite) != true)
        {
            return responses;
        }

        await DescribeChannelStatesAsync(rules, responses, states, tenantId, ct);

        var inApp = responses
            .SelectMany(r => r.Channels)
            .Where(c => c.ChannelType == ChannelType.InApp)
            .ToList();
        if (inApp.Count == 0)
        {
            return responses;
        }

        var recipients = await InAppRecipients.ListAsync(_members, tenantId, now, ct);
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
    /// Sets each channel's state, the member a channel without a recipient goes to, and who added a
    /// stamped channel and when it stops, each named as <see cref="PeopleNames.OfAsync"/> names them.
    /// </summary>
    private async Task DescribeChannelStatesAsync(
        IReadOnlyList<AlertRuleRecord> rules,
        List<AlertRuleResponse> responses,
        AlertChannelStates states,
        Guid tenantId,
        CancellationToken ct)
    {
        var described = rules.SelectMany(r => r.Channels)
            .Join(responses.SelectMany(r => r.Channels), c => c.Id, r => r.Id, (channel, response) =>
                (Channel: channel, Response: response, Verdict: states.Of(channel)))
            .ToList();

        var named = described
            .SelectMany(d => new[]
            {
                d.Verdict.State == AlertChannelState.NoRecipient ? d.Verdict.MemberSubjectId : null,
                d.Channel.AddedBySubjectId,
            })
            .OfType<Guid>()
            .ToHashSet();
        var names = await PeopleNames.OfAsync(_members, _subjects, tenantId, named, ct);

        foreach (var (channel, response, verdict) in described)
        {
            response.State = verdict.State;
            response.MemberName = verdict is { State: AlertChannelState.NoRecipient, MemberSubjectId: { } member }
                ? names.GetValueOrDefault(member)
                : null;
            response.AddedByName = channel.AddedBySubjectId is { } adder ? names.GetValueOrDefault(adder) : null;
            response.StopsAt = channel.StopsAt;
        }
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
        var members = await InAppRecipients.ListAsync(_members, _tenantAccessor.TenantId, _time.GetUtcNow().UtcDateTime, ct);
        return Ok(members
            .Select(m => new InAppRecipientResponse { SubjectId = m.Key, Name = m.Value, CanReceive = true })
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    private static AlertRuleResponse MapToResponse(
        AlertRuleRecord record,
        IReadOnlyList<RustValidationIssue>? conditionIssues,
        IReadOnlySet<Guid> lowCoveringRuleIds) => new()
    {
        CoversLow = lowCoveringRuleIds.Contains(record.Rule.Id),
        IsLastLowCover = lowCoveringRuleIds.Count == 1 && lowCoveringRuleIds.Contains(record.Rule.Id),
        Id = record.Rule.Id,
        Name = record.Rule.Name,
        Description = record.Description,
        ConditionType = record.Rule.ConditionType,
        ConditionParams = AlertRuleResponses.Json(record.Rule.ConditionParams),
        IsEnabled = record.IsEnabled,
        EnabledAt = record.Rule.EnabledAt,
        SortOrder = record.Rule.SortOrder,
        Severity = record.Rule.Severity,
        AllowThroughDnd = record.Rule.AllowThroughDnd,
        ScopeClass = record.Rule.ScopeClass,
        ManagedBy = record.ManagedBy,
        ReviewReason = AlertRuleMapper.ShownReviewReason(record),
        ConditionIssues = [.. conditionIssues ?? []],
        AutoResolveEnabled = record.Rule.AutoResolveEnabled,
        AutoResolveParams = record.Rule.AutoResolveParams is null
            ? null
            : AlertRuleResponses.Json(record.Rule.AutoResolveParams),
        OnUnknown = record.Rule.OnUnknown,
        GlucoseFreshnessMinutes = record.Rule.GlucoseFreshnessMinutes,
        ClientConfiguration = AlertRuleResponses.Json(record.Rule.ClientConfiguration),
        Channels = record.Channels
            .Select(AlertRuleResponses.Channel)
            .ToList(),
    };

    /// <summary>
    /// Returns a <c>400 BadRequest</c> when the rule contains a <c>tracker_age</c> leaf whose
    /// <c>tracker_definition_id</c> is missing, malformed, or does not exist for this tenant.
    /// Without this the rule saves fine but the engine fails closed on every reading and sweep
    /// pass, a rule that silently never fires. Returns null when the request is acceptable.
    /// </summary>
    private static async Task<BadRequestObjectResult?> RejectInvalidTrackerAgeAsync(
        ITrackerRepository trackers, IReadOnlyList<ConditionLeaf> leaves, CancellationToken ct)
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
            if (await trackers.GetDefinitionByIdAsync(definitionId, ct) is null)
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

public class AutoResolveSuggestionRequest
{
    /// <summary>A condition node as a rule stores it: its type, and its payload under that name.</summary>
    public object? Condition { get; set; }
}

public class AutoResolveSuggestionResponse
{
    /// <summary>The opposite condition node, or null when the condition has none.</summary>
    public object? Condition { get; set; }
}

/// <summary>The accepted values of <see cref="AlertRuleResponse.OnUnknown"/> and <see cref="AlertRuleResponse.GlucoseFreshnessMinutes"/>.</summary>
public class AlertRuleSettingOptionsResponse
{
    public List<string> OnUnknownValues { get; set; } = [];
    /// <summary>What a rule created without a choice gets.</summary>
    public string DefaultOnUnknown { get; set; } = AlertRuleOnUnknown.Hold;
    public int DefaultGlucoseFreshnessMinutes { get; set; }
    public int MinGlucoseFreshnessMinutes { get; set; }
    public int MaxGlucoseFreshnessMinutes { get; set; }
}

/// <summary>An <see cref="AlertPreset"/>, with its condition as a JSON object.</summary>
public class AlertPresetResponse
{
    public string Id { get; set; } = string.Empty;
    public AlertPresetGroup Group { get; set; }
    public AlertRuleSeverity Severity { get; set; }
    public bool CanWait { get; set; }
    public AlertConditionType? ConditionType { get; set; }
    public object? ConditionParams { get; set; }
    public bool AutoResolve { get; set; }
    public List<TrackerCategory> TrackerCategories { get; set; } = [];
    public int? TrackerOnlyAgeHours { get; set; }
    public StarterAlertKind? StarterKind { get; set; }
}

public class AlertRuleResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;
    public object ConditionParams { get; set; } = new { };
    public bool IsEnabled { get; set; }
    /// <summary>When the rule was last enabled; null when not recorded. Read-only.</summary>
    public DateTime? EnabledAt { get; set; }
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
    /// <summary>True when this rule alerts for low glucose: it is on, evaluates, is a low or
    /// composite rule (<see cref="ScopeClass"/>), and has a channel that delivers (<see cref="LowCoverage"/>).</summary>
    public bool CoversLow { get; set; }
    /// <summary>True when this is the only rule that covers low glucose, so turning it off or
    /// deleting it leaves the tenant with no low alert.</summary>
    public bool IsLastLowCover { get; set; }
    /// <summary>Set while the rule is on and waiting for missing data, so it can neither fire nor
    /// clear: since when, and the facts it waits for (docs/alerts/engine-semantics.md §6.4).</summary>
    public AlertHold? Hold { get; set; }
    public bool AutoResolveEnabled { get; set; }
    public object? AutoResolveParams { get; set; }
    /// <summary>What an unknown condition means to the rule's alert: <c>hold</c> keeps it as it
    /// is, <c>met</c> reads it as true, <c>not_met</c> as false (<see cref="AlertRuleOnUnknown"/>).</summary>
    public string OnUnknown { get; set; } = AlertRuleOnUnknown.Hold;
    /// <summary>How many minutes old a reading may be before the rule's glucose conditions read it
    /// unknown; null is the engine's default (docs/alerts/engine-semantics.md §3).</summary>
    public int? GlucoseFreshnessMinutes { get; set; }
    public object ClientConfiguration { get; set; } = new { };
    /// <summary>Flat list of delivery channels. Dispatched in parallel when the rule fires.</summary>
    public List<AlertRuleChannelResponse> Channels { get; set; } = [];
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
    /// <summary>One of <see cref="AlertRuleOnUnknown"/>; null is <c>hold</c>.</summary>
    public string? OnUnknown { get; set; }
    /// <summary>In <see cref="AlertRuleGlucoseFreshness"/>'s range; null is the engine's default.</summary>
    public int? GlucoseFreshnessMinutes { get; set; }
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
    /// <summary>One of <see cref="AlertRuleOnUnknown"/>; null keeps the stored value.</summary>
    public string? OnUnknown { get; set; }
    /// <summary>
    /// In <see cref="AlertRuleGlucoseFreshness"/>'s range; null keeps the stored value, so a client
    /// that does not know the bound leaves it as it is. <see cref="ClearGlucoseFreshness"/> clears it.
    /// </summary>
    public int? GlucoseFreshnessMinutes { get; set; }
    /// <summary>Returns the rule to the engine's default bound; not with <see cref="GlucoseFreshnessMinutes"/>.</summary>
    public bool ClearGlucoseFreshness { get; set; }
    public object? ClientConfiguration { get; set; }
    public List<CreateAlertRuleChannelRequest>? Channels { get; set; }
}

/// <summary>
/// Request body for the dry-run test fire endpoint. Mirrors the editor's in-memory rule
/// shape — only the fields needed to render a notification.
/// </summary>
public record TestFireDryRunRequest(
    string Name,
    AlertRuleSeverity Severity,
    List<CreateAlertRuleChannelRequest> Channels);

/// <summary>What a test fire found.</summary>
public class TestFireResponse
{
    /// <summary>The channels that were not sent because they do not deliver.</summary>
    public List<AlertChannelNotSent> NotSent { get; set; } = [];
}

#endregion
