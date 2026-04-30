using Nocturne.Core.Contracts.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Per-replay <see cref="IConditionTimerStore"/> backed by a plain dictionary. Replay
/// constructs a fresh instance per <c>ReplayAsync</c> call so sustained-condition timer
/// state never bleeds across replays or into the live tenant-scoped table.
/// </summary>
internal sealed class InMemoryConditionTimerStore : IConditionTimerStore
{
    private readonly Dictionary<(Guid RuleId, string Path), DateTime> _store = new();

    public Task<DateTime?> GetFirstTrueAsync(Guid ruleId, string path, CancellationToken ct) =>
        Task.FromResult(_store.TryGetValue((ruleId, path), out var v) ? v : (DateTime?)null);

    public Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct)
    {
        _store[(ruleId, path)] = at;
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid ruleId, string path, CancellationToken ct)
    {
        _store.Remove((ruleId, path));
        return Task.CompletedTask;
    }

    public Task ClearAllForRuleAsync(Guid ruleId, CancellationToken ct)
    {
        var toRemove = _store.Keys.Where(k => k.RuleId == ruleId).ToList();
        foreach (var k in toRemove) _store.Remove(k);
        return Task.CompletedTask;
    }

    public Task PruneToPathsAsync(Guid ruleId, IReadOnlyCollection<string> retainedPaths, CancellationToken ct)
    {
        var keep = new HashSet<string>(retainedPaths, StringComparer.Ordinal);
        var toRemove = _store.Keys.Where(k => k.RuleId == ruleId && !keep.Contains(k.Path)).ToList();
        foreach (var k in toRemove) _store.Remove(k);
        return Task.CompletedTask;
    }
}
