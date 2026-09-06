using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Manually-advanced clock, mirroring the ReplayTimeProvider inside AlertReplayService.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetUtcNow(DateTime utc) =>
        _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}

/// <summary>
/// In-memory <see cref="IConditionTimerStore"/> that records every mutation so the
/// expected snapshot can assert sustained-timer set/clear deltas (the state the host
/// persists between evaluations). Read behaviour matches the engine-internal
/// InMemoryConditionTimerStore exactly.
/// </summary>
public sealed class RecordingTimerStore : IConditionTimerStore
{
    private readonly Dictionary<(Guid RuleId, string Path), DateTime> _store = new();
    private readonly List<ExpectedTimerOp> _log = new();

    public Task<DateTime?> GetFirstTrueAsync(Guid ruleId, string path, CancellationToken ct) =>
        Task.FromResult(_store.TryGetValue((ruleId, path), out var v) ? v : (DateTime?)null);

    public Task<IReadOnlyDictionary<string, DateTime>> GetAllForRuleAsync(Guid ruleId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTime>>(
            _store.Where(kv => kv.Key.RuleId == ruleId)
                .ToDictionary(kv => kv.Key.Path, kv => kv.Value));

    public Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct)
    {
        _store[(ruleId, path)] = at;
        _log.Add(new ExpectedTimerOp("set", path, DateTime.SpecifyKind(at, DateTimeKind.Utc)));
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid ruleId, string path, CancellationToken ct)
    {
        // Only a clear that removes something is observable state change; the engine calls
        // ClearAsync unconditionally on every child-false tick, which would flood the
        // snapshot with no-ops. Record the op only when a timer actually existed.
        if (_store.Remove((ruleId, path)))
        {
            _log.Add(new ExpectedTimerOp("clear", path, null));
        }
        return Task.CompletedTask;
    }

    public Task ClearAllForRuleAsync(Guid ruleId, CancellationToken ct)
    {
        var toRemove = _store.Keys.Where(k => k.RuleId == ruleId).ToList();
        foreach (var k in toRemove)
        {
            _store.Remove(k);
            _log.Add(new ExpectedTimerOp("clear", k.Path, null));
        }
        return Task.CompletedTask;
    }

    public Task PruneToPathsAsync(Guid ruleId, IReadOnlyCollection<string> retainedPaths, CancellationToken ct)
    {
        var keep = new HashSet<string>(retainedPaths, StringComparer.Ordinal);
        var toRemove = _store.Keys.Where(k => k.RuleId == ruleId && !keep.Contains(k.Path)).ToList();
        foreach (var k in toRemove)
        {
            _store.Remove(k);
            _log.Add(new ExpectedTimerOp("clear", k.Path, null));
        }
        return Task.CompletedTask;
    }

    /// <summary>Returns the ops recorded since the last drain, then clears the log.</summary>
    public List<ExpectedTimerOp> DrainOps()
    {
        var ops = new List<ExpectedTimerOp>(_log);
        _log.Clear();
        return ops;
    }
}

/// <summary>
/// In-memory <see cref="IAlertTrackerRepository"/> with deterministic, ordinal-based
/// excursion ids so snapshots are stable across runs and engines.
/// </summary>
public sealed class InMemoryTrackerRepository : IAlertTrackerRepository
{
    private readonly Dictionary<Guid, AlertRule> _rules;
    private readonly Dictionary<Guid, AlertTrackerState> _states = new();
    private readonly Dictionary<Guid, AlertExcursion> _excursions = new();
    private readonly Dictionary<Guid, int> _excursionOrdinals = new();
    private int _nextOrdinal = 1;

    public InMemoryTrackerRepository(IEnumerable<AlertRule> rules)
    {
        _rules = rules.ToDictionary(r => r.Id);
    }

    public Task<AlertTrackerState?> GetTrackerStateAsync(Guid alertRuleId, CancellationToken ct = default) =>
        Task.FromResult(_states.TryGetValue(alertRuleId, out var s) ? s : null);

    public Task UpsertTrackerStateAsync(AlertTrackerState state, CancellationToken ct = default)
    {
        _states[state.AlertRuleId] = state;
        return Task.CompletedTask;
    }

    public Task<AlertRule?> GetRuleAsync(Guid alertRuleId, CancellationToken ct = default) =>
        Task.FromResult(_rules.TryGetValue(alertRuleId, out var r) ? r : null);

    public Task<AlertExcursion> CreateExcursionAsync(Guid alertRuleId, DateTime startedAt, CancellationToken ct = default)
    {
        var ordinal = _nextOrdinal++;
        var excursion = new AlertExcursion
        {
            Id = OrdinalGuid(ordinal),
            AlertRuleId = alertRuleId,
            StartedAt = startedAt,
        };
        _excursions[excursion.Id] = excursion;
        _excursionOrdinals[excursion.Id] = ordinal;
        return Task.FromResult(excursion);
    }

    public Task CloseExcursionAsync(Guid excursionId, DateTime endedAt, CancellationToken ct = default)
    {
        if (_excursions.TryGetValue(excursionId, out var e)) e.EndedAt = endedAt;
        return Task.CompletedTask;
    }

    public Task SetHysteresisStartedAsync(Guid excursionId, DateTime hysteresisStartedAt, CancellationToken ct = default)
    {
        if (_excursions.TryGetValue(excursionId, out var e)) e.HysteresisStartedAt = hysteresisStartedAt;
        return Task.CompletedTask;
    }

    public Task ClearHysteresisAsync(Guid excursionId, CancellationToken ct = default)
    {
        if (_excursions.TryGetValue(excursionId, out var e)) e.HysteresisStartedAt = null;
        return Task.CompletedTask;
    }

    public int? OrdinalOf(Guid? excursionId) =>
        excursionId is { } id && _excursionOrdinals.TryGetValue(id, out var ordinal) ? ordinal : null;

    private static Guid OrdinalGuid(int ordinal) =>
        Guid.Parse($"00000000-0000-0000-0001-{ordinal:D12}");
}
