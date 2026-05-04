using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// What-if replay of the tenant's enabled alert rules over a historical window.
/// Returns the events that <em>would</em> have fired had the current rule set been active.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v4/alerts/replay")]
public class AlertReplayController : ControllerBase
{
    private readonly IAlertReplayService _replayService;

    public AlertReplayController(IAlertReplayService replayService)
    {
        _replayService = replayService;
    }

    /// <summary>
    /// Replay enabled rules over a window. <c>date=null</c> replays the rolling last 24 hours;
    /// otherwise replays that calendar day in <c>timezone</c> (UTC if omitted).
    /// </summary>
    [HttpPost]
    [RemoteCommand]
    [ProducesResponseType(typeof(AlertReplayResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertReplayResult>> Replay(
        [FromBody] AlertReplayRequest request, CancellationToken ct)
    {
        var result = await _replayService.ReplayAsync(
            request.Date, request.Timezone, request.From, request.To, ct);
        return Ok(result);
    }

    /// <summary>
    /// Dry-run replay variant for the rule editor. Layers a user-provided rule definition
    /// onto the saved rule set for one call (never persisted), so authors can answer
    /// "would my new/edited rule have woken me last night?" before saving.
    /// </summary>
    [HttpPost("dry-run")]
    [RemoteCommand]
    [ProducesResponseType(typeof(AlertReplayResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertReplayResult>> ReplayDryRun(
        [FromBody] AlertReplayDryRunRequest request, CancellationToken ct)
    {
        var ruleOverride = new ReplayRuleOverride(
            Id: request.Rule.Id,
            Name: request.Rule.Name,
            ConditionType: request.Rule.ConditionType,
            ConditionParams: request.Rule.ConditionParams,
            Severity: request.Rule.Severity,
            AllowThroughDnd: request.Rule.AllowThroughDnd,
            AutoResolveEnabled: request.Rule.AutoResolveEnabled,
            AutoResolveParams: request.Rule.AutoResolveParams);

        var result = await _replayService.ReplayDryRunAsync(
            request.Date, request.Timezone, request.From, request.To, ruleOverride, ct);
        return Ok(result);
    }
}

/// <summary>
/// Request body for the alerts replay endpoint. <paramref name="From"/> and
/// <paramref name="To"/> are absolute UTC instants and take precedence over
/// <paramref name="Date"/> + <paramref name="Timezone"/> when set, allowing replay of an
/// arbitrary window (not just a calendar day).
/// </summary>
public record AlertReplayRequest(
    DateOnly? Date,
    string? Timezone,
    DateTime? From = null,
    DateTime? To = null);

/// <summary>
/// Request body for the dry-run replay endpoint. <see cref="ReplayRuleDefinition.Id"/> is
/// optional: when present and matching an existing rule it replaces it for the simulation;
/// otherwise the rule is appended for the call. <paramref name="From"/>/<paramref name="To"/>
/// behave the same as on <see cref="AlertReplayRequest"/>.
/// </summary>
public record AlertReplayDryRunRequest(
    DateOnly? Date,
    string? Timezone,
    ReplayRuleDefinition Rule,
    DateTime? From = null,
    DateTime? To = null);

/// <summary>
/// In-memory rule definition used by the dry-run endpoint. Mirrors the editor's pre-save
/// shape — the controller doesn't deserialise the condition tree itself, just the
/// <see cref="ConditionParams"/> JSON blob the rule body would have stored.
/// </summary>
public record ReplayRuleDefinition(
    Guid? Id,
    string Name,
    AlertConditionType ConditionType,
    string ConditionParams,
    AlertRuleSeverity Severity,
    bool AllowThroughDnd,
    bool AutoResolveEnabled,
    string? AutoResolveParams);
