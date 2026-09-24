using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// The <see cref="IAlertReplayEngine"/> backed by the Rust engine's replay driver
/// (<c>nocturne_alerts_replay</c>): the whole rule set and every tick in one native call.
/// </summary>
/// <remarks>
/// Rules are mapped from the snapshots the caller passes, never re-read from the database, so a
/// dry-run rule override replays as written.
/// </remarks>
internal sealed class RustAlertReplayEngine(AlertEngineErrors errors) : IAlertReplayEngine
{
    /// <summary>The <see cref="AlertEngineErrors"/> engine tag calls are recorded under.</summary>
    public string EngineTag { get; init; } = AlertEngineErrors.RustEngine;

    /// <exception cref="RustAlertEngineException">The native call failed.</exception>
    public Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        var request = new RustReplayRequest
        {
            Rules = input.Rules.Select(BuildRule).ToList(),
            Ticks = input.Ticks.Select(t => new RustReplayTick
            {
                At = DateTime.SpecifyKind(t.At, DateTimeKind.Utc),
                Context = RustEnvelopeMapper.BuildContext(t.Context),
                SuppressedRuleIds = t.SuppressedRuleIds.Count == 0 ? null : t.SuppressedRuleIds.ToList(),
            }).ToList(),
            IncludeTicks = input.IncludeTicks,
        };
        var response = errors.Track("replay", EngineTag, () => RustAlertEngine.Replay(request));
        return Task.FromResult(ToRun(response));
    }

    /// <summary>
    /// A body whose JSON does not parse goes as a JSON string. The engine cannot read a string as a
    /// payload, so it skips the rule on every tick, as the managed replay does.
    /// </summary>
    private static RustAlertRule BuildRule(AlertRuleSnapshot rule) => new()
    {
        Id = rule.Id,
        ConditionType = AlertConditionTypeNames.ToWireString(rule.ConditionType),
        ConditionParams = ParseBody(rule),
        AutoResolveEnabled = rule.AutoResolveEnabled,
        AutoResolveParams = rule.AutoResolveEnabled ? ParseAutoResolve(rule.AutoResolveParams) : null,
    };

    private static JsonElement ParseBody(AlertRuleSnapshot rule)
    {
        try
        {
            return RustEnvelopeMapper.ParseJson(ConditionTimeZones.CanonicaliseRule(rule.ConditionType, rule.ConditionParams));
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(rule.ConditionParams);
        }
    }

    private static JsonElement? ParseAutoResolve(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return RustEnvelopeMapper.ParseNode(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AlertReplayRun ToRun(RustReplayResponse response) => new(
        response.Order!,
        response.Events!.Select(e => new AlertReplayRunEvent(Utc(e.At), e.RuleId, Transition(e.Kind))).ToList(),
        response.LeafTransitions!.Select(log => new AlertReplayRuleLeafLog(
            log.RuleId,
            log.Leaves.Select(leaf => new LeafTransitionLog(
                leaf.LeafId,
                leaf.Points.Select(p => new LeafTransitionPoint(p.AtMs, p.Value)).ToList())).ToList())).ToList(),
        response.Ticks?.Select(t => new AlertReplayTickOutcome(
            Utc(t.At),
            t.Rules.Select(r => new AlertReplayRuleTick(r.RuleId, r.Skipped, r.Met ?? false, r.Firing ?? false)).ToList()))
            .ToList());

    private static AlertReplayTransition Transition(RustReplayEventKind kind) => kind switch
    {
        RustReplayEventKind.Fired => AlertReplayTransition.Fired,
        RustReplayEventKind.SuppressedByDnd => AlertReplayTransition.SuppressedByDnd,
        RustReplayEventKind.AutoResolved => AlertReplayTransition.AutoResolved,
        RustReplayEventKind.Cleared => AlertReplayTransition.Cleared,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
