using System.Security.Cryptography;
using System.Text;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Shadow-mode <see cref="IAlertReplayEngine"/>: the managed replay answers. The Rust replay then
/// runs over the same input in the background, and every difference is logged as an
/// <c>AlertEngineDivergence</c>. A Rust failure is logged and never reaches the caller.
/// </summary>
/// <remarks>
/// One comparison runs at a time. A replay requested while one is running is answered without
/// being compared, so shadow mode neither doubles a replay's latency nor queues work behind it.
/// A divergence is logged as each side's item count, a hash of its items and the first item in
/// which they differ. A whole run can hold thousands of items.
/// </remarks>
internal sealed class ShadowAlertReplayEngine(
    IAlertReplayEngine managed,
    IAlertReplayEngine shadow,
    ILogger<ShadowAlertReplayEngine> logger) : IAlertReplayEngine
{
    private const string Engine = "rust";

    private const int MaxItemLength = 200;

    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <summary>The comparison started by the latest replay that started one.</summary>
    internal Task PendingComparison { get; private set; } = Task.CompletedTask;

    public async Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct)
    {
        var managedRun = await managed.ReplayAsync(input, ct);

        if (_slot.Wait(0))
            PendingComparison = Task.Run(() => CompareAsync(input, managedRun));
        else
            logger.LogDebug("Shadow replay not compared: a comparison is already running");

        return managedRun;
    }

    private async Task CompareAsync(AlertReplayInput input, AlertReplayRun managedRun)
    {
        try
        {
            var shadowRun = await shadow.ReplayAsync(input, CancellationToken.None);
            Compare(managedRun, shadowRun);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AlertEngineShadowError replay engine={Engine}", Engine);
        }
        finally
        {
            _slot.Release();
        }
    }

    private void Compare(AlertReplayRun managedRun, AlertReplayRun shadowRun)
    {
        Compare(null, "replay.order",
            managedRun.Order.Select(id => id.ToString()).ToList(),
            shadowRun.Order.Select(id => id.ToString()).ToList());

        var managedEvents = EventsByRule(managedRun);
        var shadowEvents = EventsByRule(shadowRun);
        var managedLeaves = LeavesByRule(managedRun);
        var shadowLeaves = LeavesByRule(shadowRun);

        foreach (var ruleId in managedRun.Order.Union(shadowRun.Order))
        {
            Compare(ruleId, "replay.events",
                managedEvents.GetValueOrDefault(ruleId, []), shadowEvents.GetValueOrDefault(ruleId, []));
            Compare(ruleId, "replay.leaf_transitions",
                managedLeaves.GetValueOrDefault(ruleId, []), shadowLeaves.GetValueOrDefault(ruleId, []));
        }
    }

    private void Compare(Guid? ruleId, string field, IReadOnlyList<string> managedItems, IReadOnlyList<string> shadowItems)
    {
        var first = 0;
        while (first < managedItems.Count && first < shadowItems.Count && managedItems[first] == shadowItems[first])
            first++;
        if (first == managedItems.Count && first == shadowItems.Count)
            return;

        logger.LogWarning(
            "AlertEngineDivergence rule={RuleId} engine={Engine} field={Field} managed={Managed} rust={Rust}",
            ruleId, Engine, field, Summary(managedItems, first), Summary(shadowItems, first));
    }

    private static string Summary(IReadOnlyList<string> items, int first)
    {
        var at = first < items.Count ? Cap(items[first]) : "(end)";
        return $"{items.Count} items sha256:{Hash(items)} #{first}={at}";
    }

    private static string Cap(string item) =>
        item.Length <= MaxItemLength ? item : item[..MaxItemLength] + "...";

    private static string Hash(IReadOnlyList<string> items) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', items))))[..12];

    private static Dictionary<Guid, List<string>> EventsByRule(AlertReplayRun run) =>
        run.Events
            .GroupBy(e => e.RuleId)
            .ToDictionary(g => g.Key, g => g.Select(e => $"{e.At:O} {e.Kind}").ToList());

    private static Dictionary<Guid, List<string>> LeavesByRule(AlertReplayRun run) =>
        run.LeafTransitions.ToDictionary(
            l => l.RuleId,
            l => l.Leaves.SelectMany(leaf => leaf.Points.Select(p => $"{leaf.LeafId}@{p.AtMs}={p.Value}")).ToList());
}
