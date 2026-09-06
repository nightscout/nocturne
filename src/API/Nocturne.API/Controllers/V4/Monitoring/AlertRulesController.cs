using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

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
/// read-only credentials such as a guest-link session, which holds <c>alerts.read</c>.
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
public class AlertRulesController : ControllerBase
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly IAlertReferenceService _referenceService;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly IRuleScopeClassifier _scopeClassifier;
    private readonly ISecretEncryptionService _encryption;
    private readonly ILogger<AlertRulesController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertRulesController"/>.
    /// </summary>
    public AlertRulesController(
        ITenantDbContextFactory contextFactory,
        IAlertReferenceService referenceService,
        IAlertDeliveryService deliveryService,
        IRuleScopeClassifier scopeClassifier,
        ISecretEncryptionService encryption,
        ILogger<AlertRulesController> logger)
    {
        _contextFactory = contextFactory;
        _referenceService = referenceService;
        _deliveryService = deliveryService;
        _scopeClassifier = scopeClassifier;
        _encryption = encryption;
        _logger = logger;
    }

    /// <summary>
    /// List all alert rules for the current tenant with their flat channel list.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AlertRuleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AlertRuleResponse>>> GetRules(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var rules = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Channels)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);

        return Ok(rules.Select(MapToResponse).ToList());
    }

    /// <summary>
    /// Get a single alert rule with its flat channel list.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertRuleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> GetRule(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var rule = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Channels)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (rule is null)
            return NotFound();

        return Ok(MapToResponse(rule));
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
        if (RejectPumpModeOnGenericStateSpan(request.ConditionType, request.ConditionParams) is { } badRequest)
            return badRequest;

        // No cycle detection on create: the new id is server-generated, so the proposed tree
        // cannot reference an id it doesn't yet know. Cycles can only be introduced via PUT.
        await using var db = await _contextFactory.CreateAsync(ct);

        if (await ResolveAndValidateChannelsAsync(request.Channels, db, ct) is { } badChannel)
            return badChannel;

        if (await RejectInvalidTrackerAgeAsync(db, request.ConditionType, request.ConditionParams, ct) is { } badTracker)
            return badTracker;

        var tenantId = db.TenantId;

        var conditionParamsJson = request.ConditionParams is not null
            ? JsonSerializer.Serialize(request.ConditionParams)
            : "{}";

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
            AutoResolveParams = request.AutoResolveParams is not null
                ? JsonSerializer.Serialize(request.AutoResolveParams)
                : null,
            ClientConfiguration = request.ClientConfiguration is not null
                ? JsonSerializer.Serialize(request.ClientConfiguration)
                : "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        if (request.Channels is { Count: > 0 })
        {
            var sortIndex = 0;
            foreach (var ch in request.Channels)
            {
                rule.Channels.Add(BuildChannel(ch, rule.Id, tenantId, sortIndex++, NoRetainedSecrets));
            }
        }

        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);

        var created = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Channels)
            .FirstAsync(r => r.Id == rule.Id, ct);

        return CreatedAtAction(nameof(GetRule), new { id = created.Id }, MapToResponse(created));
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
        if (RejectPumpModeOnGenericStateSpan(request.ConditionType, request.ConditionParams) is { } badRequest)
            return badRequest;

        await using var db = await _contextFactory.CreateAsync(ct);

        if (await ResolveAndValidateChannelsAsync(request.Channels, db, ct) is { } badChannel)
            return badChannel;

        if (await RejectInvalidTrackerAgeAsync(db, request.ConditionType, request.ConditionParams, ct) is { } badTracker)
            return badTracker;

        var rule = await db.AlertRules
            .Include(r => r.Channels)
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

        var conditionParamsJson = request.ConditionParams is not null
            ? JsonSerializer.Serialize(request.ConditionParams)
            : "{}";

        rule.Name = request.Name;
        rule.Description = request.Description;
        rule.ConditionType = request.ConditionType;
        rule.ConditionParams = conditionParamsJson;
        rule.ScopeClass = _scopeClassifier.Classify(request.ConditionType, conditionParamsJson);
        rule.IsEnabled = request.IsEnabled;
        rule.SortOrder = request.SortOrder;
        rule.Severity = request.Severity ?? AlertRuleSeverity.Warning;
        rule.AllowThroughDnd = request.AllowThroughDnd;
        rule.AutoResolveEnabled = request.AutoResolveEnabled;
        rule.AutoResolveParams = request.AutoResolveParams is not null
            ? JsonSerializer.Serialize(request.AutoResolveParams)
            : null;
        rule.ClientConfiguration = request.ClientConfiguration is not null
            ? JsonSerializer.Serialize(request.ClientConfiguration)
            : "{}";
        rule.UpdatedAt = DateTime.UtcNow;

        if (request.Channels is not null)
        {
            var retainedSecrets = CollectRetainedSecrets(rule.Channels);

            // Replace the channel list wholesale. Cascade-delete on AlertRuleChannelEntity ⇒
            // AlertDeliveryEntity is configured as SetNull (not Cascade) to preserve the audit
            // trail of historical deliveries even when the source channel is reconfigured.
            db.AlertRuleChannels.RemoveRange(rule.Channels);
            rule.Channels.Clear();

            var sortIndex = 0;
            foreach (var ch in request.Channels)
            {
                rule.Channels.Add(BuildChannel(ch, rule.Id, tenantId, sortIndex++, retainedSecrets));
            }
        }

        await db.SaveChangesAsync(ct);

        var updated = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Channels)
            .FirstAsync(r => r.Id == id, ct);

        return Ok(MapToResponse(updated));
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
        await using var db = await _contextFactory.CreateAsync(ct);

        var rule = await db.AlertRules
            .Include(r => r.Channels)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (rule is null)
            return NotFound();

        rule.IsEnabled = !rule.IsEnabled;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(MapToResponse(rule));
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
        await using var db = await _contextFactory.CreateAsync(ct);

        var rule = await db.AlertRules
            .AsNoTracking()
            .Include(r => r.Channels)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (rule is null) return NotFound();

        var channels = rule.Channels
            .OrderBy(c => c.SortOrder)
            .Select(c => new AlertRuleChannelSnapshot(
                c.Id, c.AlertRuleId, c.ChannelType,
                c.Destination, c.DestinationLabel, c.SortOrder, c.Metadata, c.Secret))
            .ToList();

        await _deliveryService.TestFireAsync(rule.Id, channels, BuildTestPayload(rule, db.TenantId), ct);
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
        // would refuse to store would report success for a channel that cannot deliver.
        if (await ResolveAndValidateChannelsAsync(request.Channels, db, ct) is { } badChannel)
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
                c.DestinationLabel, i, SerializeMetadata(c.Metadata), EncryptSecret(c.Secret)))
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

    private static AlertPayload BuildTestPayload(AlertRuleEntity rule, Guid tenantId) => new()
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

    /// <summary>
    /// Fills in a DM channel's destination from the caller's linked identity on that channel's
    /// platform and rejects a channel list whose destinations cannot deliver: a
    /// <c>device_action</c> channel naming an unknown kind or capability, a channel type with no
    /// delivery path, a channel type that needs a destination and was given none, or a destination
    /// the platform adapter cannot address. Nothing downstream inspects a destination, so an
    /// unrejected one becomes a channel that stores fine and never delivers. Returns a 400
    /// <see cref="BadRequestObjectResult"/> on the first offender, or null when all channels are
    /// valid.
    /// </summary>
    private async Task<ActionResult?> ResolveAndValidateChannelsAsync(
        List<CreateAlertRuleChannelRequest>? channels, NocturneDbContext db, CancellationToken ct)
    {
        if (channels is null)
        {
            return null;
        }

        foreach (var ch in channels)
        {
            if (RejectOversizedSecret(ch) is { } badSecret)
            {
                return badSecret;
            }

            if (ch.ChannelType == ChannelType.DeviceAction)
            {
                if (RejectInvalidDeviceActionChannel(ch) is { } badDevice)
                {
                    return badDevice;
                }

                continue;
            }

            if (ChannelDestinations.SupersededBy(ch.ChannelType) is { } replacements)
            {
                return BadRequest(new
                {
                    message = $"A {WireName(ch.ChannelType)} channel has no delivery path. Use "
                        + $"{string.Join(" or ", replacements.Select(WireName))} instead.",
                });
            }

            if (ChannelDestinations.ResolvesFromLinkedIdentity(ch.ChannelType)
                && string.IsNullOrWhiteSpace(ch.Destination))
            {
                var platform = ChannelDestinations.PlatformOf(ch.ChannelType)!;
                ch.Destination = await ResolveLinkedPlatformUserIdAsync(db, platform, ct);
                if (ch.Destination is null)
                {
                    var name = char.ToUpperInvariant(platform[0]) + platform[1..];
                    return BadRequest(new
                    {
                        message = $"No linked {name} account for this user. Link {name} under "
                            + $"Connectors & Apps, or enter a {name} user ID as the destination.",
                    });
                }
            }

            if (ChannelDestinations.RequiresDestination(ch.ChannelType)
                && string.IsNullOrWhiteSpace(ch.Destination))
            {
                return BadRequest(new
                {
                    message = $"A {WireName(ch.ChannelType)} channel requires a destination.",
                });
            }

            if (!ChannelDestinations.IsWellFormed(ch.ChannelType, ch.Destination))
            {
                return BadRequest(new
                {
                    message = $"A {WireName(ch.ChannelType)} channel's destination must be "
                        + $"{ChannelDestinations.DescribeDestination(ch.ChannelType)}; "
                        + $"got '{ch.Destination}'.",
                });
            }
        }

        return null;
    }

    /// <summary>
    /// The largest signing secret accepted, measured in UTF-8 bytes because that — not the
    /// character count — is what the stored ciphertext is sized from. Stating the bound keeps a
    /// secret of legal length in non-Latin script from being discovered as a 500 at the column.
    /// </summary>
    private const int SecretMaxBytes = 256;

    private ActionResult? RejectOversizedSecret(CreateAlertRuleChannelRequest ch)
    {
        var secret = ch.Secret?.Trim();
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(secret);
        return bytes <= SecretMaxBytes
            ? null
            : BadRequest(new
            {
                message = $"A {WireName(ch.ChannelType)} channel's signing secret must be at most "
                    + $"{SecretMaxBytes} bytes once UTF-8 encoded; got {bytes}.",
            });
    }

    private ActionResult? RejectInvalidDeviceActionChannel(CreateAlertRuleChannelRequest ch)
    {
        if (string.IsNullOrWhiteSpace(ch.Destination) || !DeviceKinds.IsValid(ch.Destination))
        {
            return BadRequest(new
            {
                message = $"A device_action channel's destination must be a device kind "
                    + $"({string.Join(", ", DeviceKinds.All)}); got '{ch.Destination}'.",
            });
        }

        var requested = DeviceCapabilities.ParseRequestedCapabilities(SerializeMetadata(ch.Metadata));
        foreach (var capability in requested)
        {
            if (!DeviceCapabilities.IsKnown(capability))
            {
                return BadRequest(new
                {
                    message = $"Unknown device capability '{capability}'.",
                });
            }

            if (!DeviceCapabilities.Registry[capability].Kinds.Contains(ch.Destination))
            {
                return BadRequest(new
                {
                    message = $"Capability '{capability}' is not available on "
                        + $"device kind '{ch.Destination}'.",
                });
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the platform user ID linked to the calling subject within this tenant, or null when
    /// the subject has no active link on that platform. Resolution happens here rather than at
    /// delivery because a dispatch runs from the background orchestrator, where there is no caller
    /// to attribute a DM to — and a rule carries no owner of its own.
    /// </summary>
    private async Task<string?> ResolveLinkedPlatformUserIdAsync(
        NocturneDbContext db, string platform, CancellationToken ct)
    {
        var subjectId = HttpContext?.GetSubjectId();
        if (subjectId is null)
        {
            return null;
        }

        var tenantId = db.TenantId;
        return await db.ChatIdentityDirectory
            .Where(d => d.TenantId == tenantId
                        && d.NocturneUserId == subjectId.Value
                        && d.Platform == platform
                        && d.IsActive)
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.PlatformUserId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Serialised name of a channel type, so error text matches the request wire format.</summary>
    private static string WireName(ChannelType channelType) =>
        JsonSerializer.Serialize(channelType).Trim('"');

    private static readonly Dictionary<(ChannelType, string), string> NoRetainedSecrets = [];

    private AlertRuleChannelEntity BuildChannel(
        CreateAlertRuleChannelRequest req, Guid ruleId, Guid tenantId, int sortOrder,
        IReadOnlyDictionary<(ChannelType, string), string> retainedSecrets) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        AlertRuleId = ruleId,
        ChannelType = req.ChannelType,
        Destination = req.Destination ?? string.Empty,
        DestinationLabel = req.DestinationLabel,
        Metadata = SerializeMetadata(req.Metadata),
        Secret = ResolveSecret(req, retainedSecrets),
        SortOrder = sortOrder,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// The stored ciphertext of every channel that carries a signing secret, keyed by the pair a
    /// caller can still name after a read: an update replaces the channel list wholesale and mints
    /// new ids, and the secret is never echoed back, so a channel arriving without one has to be
    /// matched to its predecessor by type and destination.
    /// </summary>
    /// <remarks>
    /// A pair held by more than one stored secret retains none of them: the key cannot tell which
    /// of the duplicates an incoming channel descends from, and a guess would sign one receiver's
    /// alerts with another's secret. Ciphertext is compared rather than plaintext, so two channels
    /// sharing a destination are ambiguous even when the secret behind them is the same — they are
    /// re-entered rather than silently mismatched.
    /// </remarks>
    private static IReadOnlyDictionary<(ChannelType, string), string> CollectRetainedSecrets(
        IEnumerable<AlertRuleChannelEntity> channels) => channels
            .Where(c => c.Secret is not null)
            .GroupBy(c => (c.ChannelType, c.Destination))
            .Select(g => (g.Key, Secrets: g.Select(c => c.Secret!).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(g => g.Secrets.Length == 1)
            .ToDictionary(g => g.Key, g => g.Secrets[0]);

    /// <summary>
    /// Ciphertext for the channel's signing secret. A secret omitted from the request keeps the one
    /// stored against this channel type and destination (the editor cannot re-send what it was
    /// never shown); one that is empty once trimmed clears it.
    /// </summary>
    private string? ResolveSecret(
        CreateAlertRuleChannelRequest req,
        IReadOnlyDictionary<(ChannelType, string), string> retainedSecrets)
    {
        if (req.Secret is null)
        {
            return retainedSecrets.TryGetValue(
                (req.ChannelType, req.Destination ?? string.Empty), out var retained)
                ? retained
                : null;
        }

        return EncryptSecret(req.Secret);
    }

    /// <summary>
    /// Ciphertext for a caller-supplied secret, or null when it is blank. Surrounding whitespace is
    /// dropped rather than signed with: it does not survive a copy-paste round trip through the
    /// receiver's own configuration.
    /// </summary>
    private string? EncryptSecret(string? secret)
    {
        var trimmed = secret?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : _encryption.Encrypt(trimmed);
    }

    private static string? SerializeMetadata(object? metadata) =>
        metadata is not null ? JsonSerializer.Serialize(metadata) : null;

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
        AllowThroughDnd = entity.AllowThroughDnd,
        ScopeClass = entity.ScopeClass,
        ManagedBy = entity.ManagedBy,
        AutoResolveEnabled = entity.AutoResolveEnabled,
        AutoResolveParams = entity.AutoResolveParams is null
            ? null
            : DeserializeJson(entity.AutoResolveParams),
        ClientConfiguration = DeserializeJson(entity.ClientConfiguration),
        Channels = entity.Channels
            .OrderBy(c => c.SortOrder)
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

    /// <summary>
    /// Returns a <c>400 BadRequest</c> when the rule contains a <c>tracker_age</c> leaf whose
    /// <c>tracker_definition_id</c> is missing, malformed, or does not exist for this tenant.
    /// Without this the rule saves fine but the evaluator fails closed on every reading and
    /// sweep pass — a rule that silently never fires (or throws into the per-rule catch when
    /// the id can't even deserialise). Returns null when the request is acceptable.
    /// </summary>
    private static async Task<BadRequestObjectResult?> RejectInvalidTrackerAgeAsync(
        NocturneDbContext db, AlertConditionType type, object? conditionParams, CancellationToken ct)
    {
        if (conditionParams is null)
            return null;

        var definitionIds = new List<Guid>();
        if (type == AlertConditionType.TrackerAge)
        {
            try
            {
                var json = JsonSerializer.Serialize(conditionParams);
                var typed = JsonSerializer.Deserialize<TrackerAgeCondition>(json, ReferenceJsonOptions);
                if (typed is null || typed.TrackerDefinitionId == Guid.Empty)
                    return new BadRequestObjectResult("tracker_age requires a tracker_definition_id.");
                definitionIds.Add(typed.TrackerDefinitionId);
            }
            catch (JsonException)
            {
                return new BadRequestObjectResult("tracker_age requires a valid tracker_definition_id.");
            }
        }
        else
        {
            var root = TryDeserializeRoot(type, conditionParams);
            if (root is not null)
            {
                ConditionPath.Walk<object>(root, (visited, _) =>
                {
                    if (visited.TrackerAge is { } trackerAge)
                        definitionIds.Add(trackerAge.TrackerDefinitionId);
                    return null;
                });
                if (definitionIds.Contains(Guid.Empty))
                    return new BadRequestObjectResult("tracker_age requires a tracker_definition_id.");
            }
        }

        foreach (var definitionId in definitionIds)
        {
            if (!await db.TrackerDefinitions.AnyAsync(d => d.Id == definitionId, ct))
                return new BadRequestObjectResult($"Unknown tracker definition '{definitionId}'.");
        }

        return null;
    }

    /// <summary>
    /// Returns a <c>400 BadRequest</c> when the rule contains a <c>state_span_active</c> leaf
    /// with <see cref="StateSpanCategory.PumpMode"/> anywhere in the condition tree
    /// (including nested under composite/not/sustained wrappers). Pump-mode rules must use
    /// the dedicated <see cref="AlertConditionType.PumpState"/> type so the enricher loads
    /// the correct snapshot and the legacy <c>pump_suspended</c> evaluator stays uncoupled
    /// from the generic state-span dictionary. The runtime
    /// <c>StateSpanActiveEvaluator</c> fails closed for this combination, so without an
    /// upfront 400 the user gets a rule that silently never fires. Returns null when the
    /// request is acceptable.
    /// </summary>
    private BadRequestObjectResult? RejectPumpModeOnGenericStateSpan(
        AlertConditionType type, object? conditionParams)
    {
        if (conditionParams is null)
            return null;

        // Top-level state_span_active: deserialize and check directly. This path also covers
        // requests where the wrapper deserialization below would no-op for unknown shapes.
        if (type == AlertConditionType.StateSpanActive)
        {
            try
            {
                var json = JsonSerializer.Serialize(conditionParams);
                var typed = JsonSerializer.Deserialize<StateSpanActiveCondition>(json, ReferenceJsonOptions);
                if (typed is not null && typed.Category == StateSpanCategory.PumpMode)
                {
                    return BadRequest("state_span_active does not accept the PumpMode category — use pump_state instead.");
                }
            }
            catch (JsonException)
            {
                // Malformed JSON falls through to the existing rule-shape validation paths.
            }
            return null;
        }

        // Composite/not/sustained: reuse the same deserialization the cycle detector uses,
        // then walk every leaf via ConditionTreeWalker. Unknown/non-wrapper kinds yield a
        // bare ConditionNode with no payload, so the walker no-ops harmlessly.
        var root = TryDeserializeRoot(type, conditionParams);
        if (root is not null && ConditionTreeWalker.ContainsPumpModeStateSpan(root))
        {
            return BadRequest("state_span_active does not accept the PumpMode category — use pump_state instead.");
        }

        return null;
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
    /// <summary>When true, this rule still fires while the tenant is in Do Not Disturb mode.
    /// Critical rules implicitly bypass DND regardless of this flag.</summary>
    public bool AllowThroughDnd { get; set; }
    /// <summary>Low/high classification for scoped Do Not Disturb (ADR 0004), derived by the
    /// shared engine from the rule's directional leaves. Read-only — computed server-side on
    /// create/update; a scoped <c>lows</c>/<c>highs</c> window silences a rule only when its
    /// class matches.</summary>
    public RuleScopeClass ScopeClass { get; set; } = RuleScopeClass.Undirected;
    /// <summary>Owner tag when this rule is synthesised from another feature's configuration
    /// (e.g. <c>tracker:{definitionId}</c>). Null for user-authored rules. Managed rules
    /// cannot be deleted here — the owning configuration re-syncs their condition, name and
    /// severity; channels and client configuration remain user-editable.</summary>
    public string? ManagedBy { get; set; }
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
