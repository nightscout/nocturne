using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The 30s sweep and the per-reading pass evaluate the same rule from separate scopes, so
/// two <see cref="ExcursionTracker"/> instances share the singleton
/// <see cref="AlertRuleEvaluationGate"/> here, exactly as they do in the container.
/// </summary>
[Trait("Category", "Unit")]
public class ExcursionTrackerConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Concurrent_evaluations_of_one_rule_open_exactly_one_excursion()
    {
        var ruleId = Guid.NewGuid();
        // Both evaluations rendezvous inside the state read, so unguarded they would each
        // observe "idle" and open their own excursion.
        var repository = new RendezvousTrackerRepository(
            [Rule(ruleId)], participants: 2, rendezvousTimeout: TimeSpan.FromMilliseconds(500));
        var gate = new AlertRuleEvaluationGate();

        var transitions = await Task.WhenAll(
            Evaluate(repository, gate, ruleId),
            Evaluate(repository, gate, ruleId));

        repository.Excursions.Should().ContainSingle();
        transitions.Count(t => t.Type == ExcursionTransitionType.ExcursionOpened).Should().Be(1);
        transitions.Count(t => t.Type == ExcursionTransitionType.ExcursionContinues).Should().Be(1);

        var state = await repository.GetTrackerStateAsync(ruleId);
        state!.ActiveExcursionId.Should().Be(repository.Excursions.Single().Id,
            "the surviving state must reference the one excursion that was opened");
    }

    [Fact]
    public async Task Concurrent_evaluations_of_different_rules_are_not_serialised()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        // The rendezvous only completes if both rules are inside the state read at once — a
        // gate that isn't striped per rule would hold the second out until the first finished.
        var repository = new RendezvousTrackerRepository(
            [Rule(first), Rule(second)], participants: 2, rendezvousTimeout: TimeSpan.FromSeconds(5));
        var gate = new AlertRuleEvaluationGate();

        var transitions = await Task.WhenAll(
            Evaluate(repository, gate, first),
            Evaluate(repository, gate, second))
            .WaitAsync(TimeSpan.FromSeconds(30));

        repository.Overlapped.Should().BeTrue("evaluations of different rules must run concurrently");
        transitions.Should().OnlyContain(t => t.Type == ExcursionTransitionType.ExcursionOpened);
        repository.Excursions.Should().HaveCount(2);
    }

    private static Task<ExcursionTransition> Evaluate(
        IAlertTrackerRepository repository, AlertRuleEvaluationGate gate, Guid ruleId) =>
        Task.Run(() => new ExcursionTracker(
                repository, gate, new FakeTimeProvider(Now), NullLogger<ExcursionTracker>.Instance)
            .ProcessEvaluationAsync(ruleId, true, CancellationToken.None));

    private static AlertRule Rule(Guid id) => new()
    {
        Id = id,
        Name = "Test Rule",
        ConfirmationReadings = 1,
        HysteresisMinutes = 5,
    };

    /// <summary>
    /// In-memory tracker persistence whose state read blocks until every participant has
    /// entered it (or the rendezvous times out), turning the read-modify-write window into a
    /// deterministic one instead of one that depends on thread scheduling.
    /// </summary>
    private sealed class RendezvousTrackerRepository(
        IReadOnlyList<AlertRule> rules, int participants, TimeSpan rendezvousTimeout)
        : IAlertTrackerRepository
    {
        private readonly TaskCompletionSource _allInside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _sync = new();
        private readonly Dictionary<Guid, AlertTrackerState> _states = [];
        private int _inside;

        public List<AlertExcursion> Excursions { get; } = [];

        /// <summary>Whether every participant was inside the state read at the same moment.</summary>
        public bool Overlapped { get; private set; }

        public async Task<AlertTrackerState?> GetTrackerStateAsync(Guid alertRuleId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _inside) >= participants)
            {
                Overlapped = true;
                _allInside.TrySetResult();
            }

            try
            {
                await _allInside.Task.WaitAsync(rendezvousTimeout, ct);
            }
            catch (TimeoutException)
            {
                // Serialised callers never meet; carry on with whatever state is committed.
            }

            Interlocked.Decrement(ref _inside);

            lock (_sync)
            {
                return _states.TryGetValue(alertRuleId, out var state)
                    ? new AlertTrackerState
                    {
                        AlertRuleId = state.AlertRuleId,
                        State = state.State,
                        ConfirmationCount = state.ConfirmationCount,
                        ActiveExcursionId = state.ActiveExcursionId,
                        UpdatedAt = state.UpdatedAt,
                    }
                    : null;
            }
        }

        public Task UpsertTrackerStateAsync(AlertTrackerState state, CancellationToken ct = default)
        {
            lock (_sync) _states[state.AlertRuleId] = state;
            return Task.CompletedTask;
        }

        public Task<AlertRule?> GetRuleAsync(Guid alertRuleId, CancellationToken ct = default) =>
            Task.FromResult(rules.FirstOrDefault(r => r.Id == alertRuleId));

        public Task<AlertExcursion> CreateExcursionAsync(
            Guid alertRuleId, DateTime startedAt, CancellationToken ct = default)
        {
            var excursion = new AlertExcursion
            {
                Id = Guid.CreateVersion7(),
                AlertRuleId = alertRuleId,
                StartedAt = startedAt,
            };

            lock (_sync) Excursions.Add(excursion);
            return Task.FromResult(excursion);
        }

        public Task CloseExcursionAsync(Guid excursionId, DateTime endedAt, CancellationToken ct = default)
        {
            lock (_sync)
            {
                var excursion = Excursions.FirstOrDefault(e => e.Id == excursionId);
                if (excursion is not null) excursion.EndedAt = endedAt;
            }

            return Task.CompletedTask;
        }

        public Task SetHysteresisStartedAsync(
            Guid excursionId, DateTime hysteresisStartedAt, CancellationToken ct = default)
        {
            lock (_sync)
            {
                var excursion = Excursions.FirstOrDefault(e => e.Id == excursionId);
                if (excursion is not null) excursion.HysteresisStartedAt = hysteresisStartedAt;
            }

            return Task.CompletedTask;
        }

        public Task ClearHysteresisAsync(Guid excursionId, CancellationToken ct = default)
        {
            lock (_sync)
            {
                var excursion = Excursions.FirstOrDefault(e => e.Id == excursionId);
                if (excursion is not null) excursion.HysteresisStartedAt = null;
            }

            return Task.CompletedTask;
        }
    }
}
