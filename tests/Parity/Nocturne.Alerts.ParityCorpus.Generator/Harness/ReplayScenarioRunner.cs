using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Drives a replay scenario through an <see cref="IAlertReplayEngine"/> and produces its expected
/// snapshot. The generator uses <see cref="Managed"/>, the C# replay, as the authority.
/// </summary>
public static class ReplayScenarioRunner
{
    public static IAlertReplayEngine Managed() =>
        new ManagedAlertReplayEngine(NullLogger<ManagedAlertReplayEngine>.Instance);

    /// <summary>The Rust replay behind the same adapter the API uses; needs the native library.</summary>
    public static IAlertReplayEngine Rust() =>
        new RustAlertReplayEngine(new AlertEngineErrors(
            new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            TimeProvider.System));

    public static async Task<ReplayExpectedFile> RunAsync(
        IAlertReplayEngine engine, ReplayScenarioFile scenario, CancellationToken ct)
    {
        var rules = scenario.Rules.Select(ToSnapshot).ToList();
        var ticks = scenario.Ticks
            .Select(t => new AlertReplayTick(
                DateTime.SpecifyKind(t.At, DateTimeKind.Utc),
                ScenarioConversions.ToSensorContext(t.Context),
                (t.SuppressedRuleIds ?? []).ToHashSet()))
            .ToList();

        var run = await engine.ReplayAsync(new AlertReplayInput(rules, ticks, IncludeTicks: true), ct);

        return new ReplayExpectedFile
        {
            Scenario = scenario.Name,
            Order = run.Order.ToList(),
            Events = run.Events.Select(e => new ReplayExpectedEvent(Utc(e.At), e.RuleId, KindWire(e.Kind))).ToList(),
            LeafTransitions = run.LeafTransitions
                .Select(log => new ReplayExpectedLeafLog(
                    log.RuleId,
                    log.Leaves.Select(leaf => new ReplayExpectedLeaf(
                        leaf.LeafId,
                        leaf.Points.Select(p => new ReplayExpectedPoint(p.AtMs, p.Value)).ToList())).ToList()))
                .ToList(),
            Ticks = run.Ticks!
                .Select(t => new ReplayExpectedTick(
                    Utc(t.At),
                    t.Rules.Select(r => r.Skipped
                        ? new ReplayExpectedRuleTick { RuleId = r.RuleId, Skipped = true }
                        : new ReplayExpectedRuleTick { RuleId = r.RuleId, Met = r.Met, Firing = r.Firing }).ToList()))
                .ToList(),
        };
    }

    private static AlertRuleSnapshot ToSnapshot(ScenarioRule rule)
    {
        var alertRule = ScenarioConversions.ToAlertRule(rule);
        return new AlertRuleSnapshot(
            alertRule.Id, Guid.Empty, alertRule.Name, alertRule.ConditionType, alertRule.ConditionParams,
            AlertRuleSeverity.Warning, "{}", 0, alertRule.AutoResolveEnabled, alertRule.AutoResolveParams);
    }

    private static string KindWire(AlertReplayTransition kind) => kind switch
    {
        AlertReplayTransition.Fired => "fired",
        AlertReplayTransition.SuppressedByDnd => "suppressed_by_dnd",
        AlertReplayTransition.AutoResolved => "auto_resolved",
        AlertReplayTransition.Cleared => "cleared",
        _ => throw new InvalidOperationException($"Unknown replay transition {kind}"),
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
