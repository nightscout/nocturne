using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// The <see cref="IExcursionTracker"/> seam over one persistence path. Every scenario runs once
/// with the managed decider and once with the Rust one, and both must leave the same excursion
/// rows and tracker state.
/// </summary>
public class ExcursionTrackerSeamTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    public enum Engine { Managed, Rust }

    private sealed class Fixture
    {
        public Fixture(Engine engine, int confirmationReadings = 1, int hysteresisMinutes = 0)
        {
            Time.SetUtcNow(T0);
            Repository = new RecordingTrackerRepository(new AlertRule
            {
                Id = RuleId,
                Name = "low",
                ConfirmationReadings = confirmationReadings,
                HysteresisMinutes = hysteresisMinutes,
            });
            IExcursionDecider decider = engine == Engine.Managed
                ? ManagedExcursionDecider.Instance
                : new RustExcursionDecider(new AlertEngineErrors(new TestMeterFactory(), Time), AlertEngineErrors.RustEngine);
            Tracker = new ExcursionTracker(
                Repository, new AlertRuleEvaluationGate(), Time, NullLogger<ExcursionTracker>.Instance, decider);
        }

        public ManualTimeProvider Time { get; } = new();

        public RecordingTrackerRepository Repository { get; }

        public ExcursionTracker Tracker { get; }

        public AlertTrackerState? State => Repository.States.GetValueOrDefault(RuleId);

        public Task<ExcursionTransition> Process(bool met, bool autoResolveMet = false) =>
            Tracker.ProcessEvaluationAsync(RuleId, met, _ => Task.FromResult(autoResolveMet), CancellationToken.None);

        public void At(TimeSpan offset) => Time.SetUtcNow(T0 + offset);
    }

    [Fact] public Task Opens_an_excursion_managed() => Opens_an_excursion(Engine.Managed);
    [NativeFact] public Task Opens_an_excursion_rust() => Opens_an_excursion(Engine.Rust);

    private static async Task Opens_an_excursion(Engine engine)
    {
        var f = new Fixture(engine);

        var opened = await f.Process(true);

        opened.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        var excursion = f.Repository.Excursions.Values.Should().ContainSingle().Subject;
        opened.ExcursionId.Should().Be(excursion.Id);
        excursion.StartedAt.Should().Be(T0);
        f.State.Should().BeEquivalentTo(new { State = "active", ConfirmationCount = 0, ActiveExcursionId = excursion.Id, UpdatedAt = T0 });
    }

    [Fact] public Task Confirms_before_opening_managed() => Confirms_before_opening(Engine.Managed);
    [NativeFact] public Task Confirms_before_opening_rust() => Confirms_before_opening(Engine.Rust);

    private static async Task Confirms_before_opening(Engine engine)
    {
        var f = new Fixture(engine, confirmationReadings: 2);

        (await f.Process(true)).Type.Should().Be(ExcursionTransitionType.None);
        f.State!.State.Should().Be("confirming");
        f.State.ConfirmationCount.Should().Be(1);
        f.Repository.Excursions.Should().BeEmpty();

        (await f.Process(true)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        f.Repository.Excursions.Should().ContainSingle();
    }

    [Fact] public Task Continues_the_open_excursion_managed() => Continues_the_open_excursion(Engine.Managed);
    [NativeFact] public Task Continues_the_open_excursion_rust() => Continues_the_open_excursion(Engine.Rust);

    private static async Task Continues_the_open_excursion(Engine engine)
    {
        var f = new Fixture(engine);
        var opened = await f.Process(true);
        f.At(TimeSpan.FromMinutes(5));

        var continued = await f.Process(true);

        continued.Should().Be(new ExcursionTransition(ExcursionTransitionType.ExcursionContinues, opened.ExcursionId));
        f.Repository.Excursions.Should().ContainSingle();
        f.State!.UpdatedAt.Should().Be(T0, "a continuing excursion changes no stored row");
    }

    [Fact] public Task Starts_and_resumes_hysteresis_managed() => Starts_and_resumes_hysteresis(Engine.Managed);
    [NativeFact] public Task Starts_and_resumes_hysteresis_rust() => Starts_and_resumes_hysteresis(Engine.Rust);

    private static async Task Starts_and_resumes_hysteresis(Engine engine)
    {
        var f = new Fixture(engine, hysteresisMinutes: 10);
        var opened = await f.Process(true);
        var excursion = f.Repository.Excursions[opened.ExcursionId!.Value];
        f.At(TimeSpan.FromMinutes(1));

        var started = await f.Process(false);

        started.Should().Be(new ExcursionTransition(ExcursionTransitionType.HysteresisStarted, opened.ExcursionId));
        excursion.HysteresisStartedAt.Should().Be(T0.AddMinutes(1));
        f.State!.State.Should().Be("hysteresis");
        f.State.HysteresisStartedAt.Should().Be(T0.AddMinutes(1));

        f.At(TimeSpan.FromMinutes(2));
        var resumed = await f.Process(true);

        resumed.Should().Be(new ExcursionTransition(ExcursionTransitionType.HysteresisResumed, opened.ExcursionId));
        excursion.HysteresisStartedAt.Should().BeNull();
        f.State!.State.Should().Be("active");
        f.State.HysteresisStartedAt.Should().BeNull();
        f.State.ActiveExcursionId.Should().Be(opened.ExcursionId);
    }

    [Fact] public Task Closes_elapsed_hysteresis_only_once_the_window_has_passed_managed() =>
        Closes_elapsed_hysteresis_only_once_the_window_has_passed(Engine.Managed);
    [NativeFact] public Task Closes_elapsed_hysteresis_only_once_the_window_has_passed_rust() =>
        Closes_elapsed_hysteresis_only_once_the_window_has_passed(Engine.Rust);

    private static async Task Closes_elapsed_hysteresis_only_once_the_window_has_passed(Engine engine)
    {
        var f = new Fixture(engine, hysteresisMinutes: 10);
        var opened = await f.Process(true);
        await f.Process(false);
        var writes = f.Repository.Writes.Count;

        f.At(TimeSpan.FromMinutes(9));
        (await f.Tracker.CloseElapsedHysteresisAsync(RuleId, CancellationToken.None))
            .Type.Should().Be(ExcursionTransitionType.None);
        f.Repository.Writes.Should().HaveCount(writes, "an unchanged state is not rewritten");

        f.At(TimeSpan.FromMinutes(10));
        var closed = await f.Tracker.CloseElapsedHysteresisAsync(RuleId, CancellationToken.None);

        closed.Should().Be(new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, opened.ExcursionId, ExcursionCloseReason.Hysteresis));
        f.Repository.Excursions[opened.ExcursionId!.Value].EndedAt.Should().Be(T0.AddMinutes(10));
        f.State.Should().BeEquivalentTo(new
        {
            State = "idle", ConfirmationCount = 0, ActiveExcursionId = (Guid?)null,
            HysteresisStartedAt = (DateTime?)null, UpdatedAt = T0.AddMinutes(10),
        });
    }

    [Fact] public Task Close_elapsed_hysteresis_persists_an_adopted_start_managed() =>
        Close_elapsed_hysteresis_persists_an_adopted_start(Engine.Managed);
    [NativeFact] public Task Close_elapsed_hysteresis_persists_an_adopted_start_rust() =>
        Close_elapsed_hysteresis_persists_an_adopted_start(Engine.Rust);

    private static async Task Close_elapsed_hysteresis_persists_an_adopted_start(Engine engine)
    {
        var f = new Fixture(engine, hysteresisMinutes: 10);
        var opened = await f.Process(true);
        var legacy = f.State!;
        f.Repository.States[RuleId] = new AlertTrackerState
        {
            AlertRuleId = RuleId,
            State = "hysteresis",
            ActiveExcursionId = legacy.ActiveExcursionId,
            UpdatedAt = T0,
        };
        f.At(TimeSpan.FromMinutes(3));

        (await f.Tracker.CloseElapsedHysteresisAsync(RuleId, CancellationToken.None))
            .Type.Should().Be(ExcursionTransitionType.None);

        f.State.Should().BeEquivalentTo(new
        {
            State = "hysteresis", HysteresisStartedAt = (DateTime?)T0, UpdatedAt = T0,
            ActiveExcursionId = opened.ExcursionId,
        });
    }

    [Fact] public Task An_unknown_stored_state_reads_as_idle_or_active_managed() =>
        An_unknown_stored_state_reads_as_idle_or_active(Engine.Managed);
    [NativeFact] public Task An_unknown_stored_state_reads_as_idle_or_active_rust() =>
        An_unknown_stored_state_reads_as_idle_or_active(Engine.Rust);

    private static async Task An_unknown_stored_state_reads_as_idle_or_active(Engine engine)
    {
        var f = new Fixture(engine, confirmationReadings: 1, hysteresisMinutes: 10);
        var opened = await f.Process(true);
        AlertTrackerState Unknown(Guid? excursion) => new()
        {
            AlertRuleId = RuleId,
            State = "paused",
            ConfirmationCount = 2,
            ActiveExcursionId = excursion,
            UpdatedAt = T0,
            HysteresisStartedAt = T0,
            AwaitingRearm = true,
        };

        f.Repository.States[RuleId] = Unknown(opened.ExcursionId);
        (await f.Tracker.GetActiveExcursionIdAsync(RuleId, CancellationToken.None)).Should().Be(opened.ExcursionId);
        f.At(TimeSpan.FromMinutes(1));
        (await f.Process(false)).Should().Be(
            new ExcursionTransition(ExcursionTransitionType.HysteresisStarted, opened.ExcursionId));
        f.State.Should().BeEquivalentTo(new
        {
            State = "hysteresis", ConfirmationCount = 0, ActiveExcursionId = opened.ExcursionId,
            HysteresisStartedAt = (DateTime?)T0.AddMinutes(1), AwaitingRearm = false,
        });

        f.Repository.States[RuleId] = Unknown(null);
        f.At(TimeSpan.FromMinutes(2));
        (await f.Process(true)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        f.State.Should().BeEquivalentTo(new { State = "active", ConfirmationCount = 0, AwaitingRearm = false });
    }

    [Fact] public Task An_auto_resolved_rule_holds_while_both_trees_hold_managed() =>
        An_auto_resolved_rule_holds_while_both_trees_hold(Engine.Managed);
    [NativeFact] public Task An_auto_resolved_rule_holds_while_both_trees_hold_rust() =>
        An_auto_resolved_rule_holds_while_both_trees_hold(Engine.Rust);

    private static async Task An_auto_resolved_rule_holds_while_both_trees_hold(Engine engine)
    {
        var f = new Fixture(engine);
        await f.Process(true);
        await f.Tracker.ForceCloseAsync(RuleId, ExcursionCloseReason.AutoResolve, CancellationToken.None);

        f.At(TimeSpan.FromMinutes(1));
        (await f.Process(true, autoResolveMet: true)).Type.Should().Be(ExcursionTransitionType.None);
        f.State!.AwaitingRearm.Should().BeTrue();

        f.At(TimeSpan.FromMinutes(2));
        (await f.Process(true, autoResolveMet: false)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        f.State.Should().BeEquivalentTo(new { State = "active", AwaitingRearm = false });
    }

    [Theory]
    [InlineData(ExcursionCloseReason.Manual)]
    [InlineData(ExcursionCloseReason.AutoResolve)]
    [InlineData(ExcursionCloseReason.RuleDisabled)]
    public Task Force_close_closes_from_any_open_state_managed(ExcursionCloseReason reason) =>
        Force_close_closes_from_any_open_state(Engine.Managed, reason);

    [NativeTheory]
    [InlineData(ExcursionCloseReason.Manual)]
    [InlineData(ExcursionCloseReason.AutoResolve)]
    [InlineData(ExcursionCloseReason.RuleDisabled)]
    public Task Force_close_closes_from_any_open_state_rust(ExcursionCloseReason reason) =>
        Force_close_closes_from_any_open_state(Engine.Rust, reason);

    private static async Task Force_close_closes_from_any_open_state(Engine engine, ExcursionCloseReason reason)
    {
        var f = new Fixture(engine, hysteresisMinutes: 10);
        var first = await f.Process(true);
        f.At(TimeSpan.FromMinutes(1));

        var fromActive = await f.Tracker.ForceCloseAsync(RuleId, reason, CancellationToken.None);

        fromActive.Should().Be(new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, first.ExcursionId, reason));
        f.Repository.Excursions[first.ExcursionId!.Value].EndedAt.Should().Be(T0.AddMinutes(1));
        f.State.Should().BeEquivalentTo(new
        {
            State = "idle", ActiveExcursionId = (Guid?)null, UpdatedAt = T0.AddMinutes(1),
            AwaitingRearm = reason == ExcursionCloseReason.AutoResolve,
        });

        await f.Process(false);
        var second = await f.Process(true);
        await f.Process(false);
        var fromHysteresis = await f.Tracker.ForceCloseAsync(RuleId, reason, CancellationToken.None);

        fromHysteresis.Should().Be(new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, second.ExcursionId, reason));
        f.State!.HysteresisStartedAt.Should().BeNull();
    }

    [Fact]
    public void Rule_disabled_crosses_the_rust_close_reason_wire()
    {
        RustEnvelopeMapper.CloseReasonToRust(ExcursionCloseReason.RuleDisabled)
            .Should().Be(RustCloseReason.RuleDisabled);
        RustEnvelopeMapper.CloseReasonFromWire(RustCloseReason.RuleDisabled)
            .Should().Be(ExcursionCloseReason.RuleDisabled);
    }

    [Fact] public Task Force_close_without_an_excursion_writes_nothing_managed() =>
        Force_close_without_an_excursion_writes_nothing(Engine.Managed);
    [NativeFact] public Task Force_close_without_an_excursion_writes_nothing_rust() =>
        Force_close_without_an_excursion_writes_nothing(Engine.Rust);

    private static async Task Force_close_without_an_excursion_writes_nothing(Engine engine)
    {
        var f = new Fixture(engine, confirmationReadings: 3);
        (await f.Tracker.ForceCloseAsync(RuleId, ExcursionCloseReason.Manual, CancellationToken.None))
            .Type.Should().Be(ExcursionTransitionType.None);
        await f.Process(true);
        var writes = f.Repository.Writes.Count;

        (await f.Tracker.ForceCloseAsync(RuleId, ExcursionCloseReason.Manual, CancellationToken.None))
            .Type.Should().Be(ExcursionTransitionType.None);

        f.Repository.Writes.Should().HaveCount(writes);
        f.State!.State.Should().Be("confirming");
    }

    [Fact] public Task A_state_changed_only_in_its_timestamp_is_not_written_managed() =>
        A_state_changed_only_in_its_timestamp_is_not_written(Engine.Managed);
    [NativeFact] public Task A_state_changed_only_in_its_timestamp_is_not_written_rust() =>
        A_state_changed_only_in_its_timestamp_is_not_written(Engine.Rust);

    private static async Task A_state_changed_only_in_its_timestamp_is_not_written(Engine engine)
    {
        var f = new Fixture(engine);
        await f.Process(false);
        f.Repository.Writes.Should().Equal("upsert");

        f.At(TimeSpan.FromMinutes(1));
        await f.Process(false);
        f.Repository.Writes.Should().Equal("upsert");
        f.Repository.Transactions.Should().Be(1);

        f.At(TimeSpan.FromMinutes(2));
        await f.Process(true);
        var (writes, transactions) = (f.Repository.Writes.Count, f.Repository.Transactions);

        f.At(TimeSpan.FromMinutes(3));
        (await f.Process(true)).Type.Should().Be(ExcursionTransitionType.ExcursionContinues);
        (await f.Tracker.CloseElapsedHysteresisAsync(RuleId, CancellationToken.None))
            .Type.Should().Be(ExcursionTransitionType.None);

        f.Repository.Writes.Should().HaveCount(writes);
        f.Repository.Transactions.Should().Be(transactions);
        f.State!.UpdatedAt.Should().Be(T0 + TimeSpan.FromMinutes(2));
    }

    [Fact] public Task Each_transition_writes_in_one_transaction_managed() =>
        Each_transition_writes_in_one_transaction(Engine.Managed);
    [NativeFact] public Task Each_transition_writes_in_one_transaction_rust() =>
        Each_transition_writes_in_one_transaction(Engine.Rust);

    private static async Task Each_transition_writes_in_one_transaction(Engine engine)
    {
        var f = new Fixture(engine);

        await f.Process(true);
        await f.Process(false);
        await f.Tracker.CloseElapsedHysteresisAsync(RuleId, CancellationToken.None);

        f.Repository.WritesOutsideTransaction.Should().Be(0);
        f.Repository.Transactions.Should().Be(3);
        f.Repository.Writes.Should().Equal(
            "create", "upsert",
            "set_hysteresis", "upsert",
            "close", "upsert");
    }
}

/// <summary>
/// In-memory <see cref="IAlertTrackerRepository"/> that records each write and whether it ran
/// inside <see cref="ExecuteInTransactionAsync{T}"/>.
/// </summary>
internal sealed class RecordingTrackerRepository(params AlertRule[] rules) : IAlertTrackerRepository
{
    private readonly Dictionary<Guid, AlertRule> _rules = rules.ToDictionary(r => r.Id);
    private bool _inTransaction;

    public Dictionary<Guid, AlertTrackerState> States { get; } = new();

    public Dictionary<Guid, AlertExcursion> Excursions { get; } = new();

    public List<string> Writes { get; } = [];

    public int Transactions { get; private set; }

    public int WritesOutsideTransaction { get; private set; }

    public Task<AlertTrackerState?> GetTrackerStateAsync(Guid alertRuleId, CancellationToken ct = default) =>
        Task.FromResult(States.GetValueOrDefault(alertRuleId));

    public Task UpsertTrackerStateAsync(AlertTrackerState state, CancellationToken ct = default)
    {
        Write("upsert");
        States[state.AlertRuleId] = state;
        return Task.CompletedTask;
    }

    public Task<AlertRule?> GetRuleAsync(Guid alertRuleId, CancellationToken ct = default) =>
        Task.FromResult(_rules.GetValueOrDefault(alertRuleId));

    public Task<AlertExcursion> CreateExcursionAsync(Guid alertRuleId, DateTime startedAt, CancellationToken ct = default)
    {
        Write("create");
        var excursion = new AlertExcursion { Id = Guid.CreateVersion7(), AlertRuleId = alertRuleId, StartedAt = startedAt };
        Excursions[excursion.Id] = excursion;
        return Task.FromResult(excursion);
    }

    public Task<AlertExcursion?> GetExcursionAsync(Guid excursionId, CancellationToken ct = default) =>
        Task.FromResult(Excursions.GetValueOrDefault(excursionId));

    public Task CloseExcursionAsync(Guid excursionId, DateTime endedAt, CancellationToken ct = default)
    {
        Write("close");
        Excursions[excursionId].EndedAt = endedAt;
        return Task.CompletedTask;
    }

    public Task SetHysteresisStartedAsync(Guid excursionId, DateTime hysteresisStartedAt, CancellationToken ct = default)
    {
        Write("set_hysteresis");
        Excursions[excursionId].HysteresisStartedAt = hysteresisStartedAt;
        return Task.CompletedTask;
    }

    public Task ClearHysteresisAsync(Guid excursionId, CancellationToken ct = default)
    {
        Write("clear_hysteresis");
        Excursions[excursionId].HysteresisStartedAt = null;
        return Task.CompletedTask;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        Func<T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken ct = default)
    {
        Transactions++;
        _inTransaction = true;
        try
        {
            return await work(ct);
        }
        finally
        {
            _inTransaction = false;
        }
    }

    private void Write(string operation)
    {
        Writes.Add(operation);
        if (!_inTransaction)
            WritesOutsideTransaction++;
    }
}
