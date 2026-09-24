using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class ExcursionTrackerTests
{
    private readonly Mock<IAlertTrackerRepository> _mockRepo;
    private readonly FakeTimeProvider _timeProvider;
    private readonly ExcursionTracker _tracker;
    private readonly Guid _ruleId = Guid.NewGuid();

    // Default rule with confirmation=3, hysteresis=5 min
    private readonly AlertRule _defaultRule;

    public ExcursionTrackerTests()
    {
        // CallBase runs the interface's default ExecuteInTransactionAsync, which runs its work.
        _mockRepo = new Mock<IAlertTrackerRepository> { CallBase = true };
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));

        var logger = new Mock<ILogger<ExcursionTracker>>();
        _tracker = new ExcursionTracker(
            _mockRepo.Object, new AlertRuleEvaluationGate(), _timeProvider, logger.Object);

        _defaultRule = new AlertRule
        {
            Id = _ruleId,
            Name = "Test Rule",
            ConfirmationReadings = 3,
            HysteresisMinutes = 5,
        };
    }

    private void SetupRule(AlertRule? rule = null)
    {
        var r = rule ?? _defaultRule;
        _mockRepo.Setup(x => x.GetRuleAsync(r.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(r);
    }

    private void SetupTrackerState(AlertTrackerState? state)
    {
        _mockRepo.Setup(x => x.GetTrackerStateAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);
    }

    #region IDLE state transitions

    [Fact]
    public async Task Idle_FalseEvaluation_StaysIdle()
    {
        SetupRule();
        SetupTrackerState(null); // No existing state -> idle
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);
        result.ExcursionId.Should().BeNull();
    }

    [Fact]
    public async Task Idle_TrueEvaluation_WithConfirmationGreaterThan1_TransitionsToConfirming()
    {
        SetupRule();
        SetupTrackerState(null);
        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);
        savedState.Should().NotBeNull();
        savedState!.State.Should().Be("confirming");
        savedState.ConfirmationCount.Should().Be(1);
    }

    [Fact]
    public async Task Idle_TrueEvaluation_WithConfirmation1_GoesDirectlyToActive()
    {
        var rule = new AlertRule
        {
            Id = _ruleId,
            Name = "Immediate Rule",
            ConfirmationReadings = 1,
            HysteresisMinutes = 5,
        };
        _mockRepo.Setup(x => x.GetRuleAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rule);
        SetupTrackerState(null);

        var excursionId = Guid.NewGuid();
        _mockRepo.Setup(x => x.CreateExcursionAsync(_ruleId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertExcursion { Id = excursionId, AlertRuleId = _ruleId });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        result.ExcursionId.Should().Be(excursionId);
        savedState!.State.Should().Be("active");
        savedState.ActiveExcursionId.Should().Be(excursionId);
    }

    #endregion

    #region CONFIRMING state transitions

    [Fact]
    public async Task Confirming_FalseEvaluation_ResetsToIdle()
    {
        SetupRule();
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "confirming",
            ConfirmationCount = 2,
        });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);
        savedState!.State.Should().Be("idle");
        savedState.ConfirmationCount.Should().Be(0);
    }

    [Fact]
    public async Task Confirming_TrueEvaluation_IncreasesCounter()
    {
        SetupRule(); // confirmation_readings = 3
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "confirming",
            ConfirmationCount = 1,
        });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);
        savedState!.State.Should().Be("confirming");
        savedState.ConfirmationCount.Should().Be(2);
    }

    [Fact]
    public async Task Confirming_ReachesThreshold_OpensExcursion()
    {
        SetupRule(); // confirmation_readings = 3
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "confirming",
            ConfirmationCount = 2, // One more needed
        });

        var excursionId = Guid.NewGuid();
        _mockRepo.Setup(x => x.CreateExcursionAsync(_ruleId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertExcursion { Id = excursionId, AlertRuleId = _ruleId });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        result.ExcursionId.Should().Be(excursionId);
        savedState!.State.Should().Be("active");
        savedState.ActiveExcursionId.Should().Be(excursionId);
    }

    [Fact]
    public async Task ConfirmationCounter_PreservedBetweenCalls()
    {
        SetupRule(); // confirmation_readings = 3
        SetupTrackerState(null);

        // Track saved states across calls
        var savedStates = new List<AlertTrackerState>();
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) =>
            {
                savedStates.Add(new AlertTrackerState
                {
                    AlertRuleId = s.AlertRuleId,
                    State = s.State,
                    ConfirmationCount = s.ConfirmationCount,
                    ActiveExcursionId = s.ActiveExcursionId,
                    UpdatedAt = s.UpdatedAt,
                });
                // Update the mock to return this state on next call
                _mockRepo.Setup(x => x.GetTrackerStateAsync(_ruleId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(s);
            })
            .Returns(Task.CompletedTask);

        var excursionId = Guid.NewGuid();
        _mockRepo.Setup(x => x.CreateExcursionAsync(_ruleId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertExcursion { Id = excursionId, AlertRuleId = _ruleId });

        // Call 1: idle -> confirming (count=1)
        var r1 = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);
        r1.Type.Should().Be(ExcursionTransitionType.None);
        savedStates[0].ConfirmationCount.Should().Be(1);

        // Call 2: confirming -> confirming (count=2)
        var r2 = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);
        r2.Type.Should().Be(ExcursionTransitionType.None);
        savedStates[1].ConfirmationCount.Should().Be(2);

        // Call 3: confirming -> active (count reaches 3)
        var r3 = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);
        r3.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        r3.ExcursionId.Should().Be(excursionId);
    }

    #endregion

    #region ACTIVE state transitions

    [Fact]
    public async Task Active_TrueEvaluation_ContinuesExcursion()
    {
        SetupRule();
        var excursionId = Guid.NewGuid();
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "active",
            ActiveExcursionId = excursionId,
        });
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionContinues);
        result.ExcursionId.Should().Be(excursionId);
    }

    [Fact]
    public async Task Active_FalseEvaluation_StartsHysteresis()
    {
        SetupRule();
        var excursionId = Guid.NewGuid();
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "active",
            ActiveExcursionId = excursionId,
        });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.HysteresisStarted);
        result.ExcursionId.Should().Be(excursionId);
        savedState!.State.Should().Be("hysteresis");

        _mockRepo.Verify(
            x => x.SetHysteresisStartedAsync(excursionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region HYSTERESIS state transitions

    [Fact]
    public async Task Hysteresis_TrueEvaluation_ResumesExcursion()
    {
        SetupRule();
        var excursionId = Guid.NewGuid();
        var hysteresisStart = _timeProvider.GetUtcNow().UtcDateTime;

        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = excursionId,
            UpdatedAt = hysteresisStart,
        });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        // Advance time by 2 minutes (within 5 min hysteresis)
        _timeProvider.Advance(TimeSpan.FromMinutes(2));

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.HysteresisResumed);
        result.ExcursionId.Should().Be(excursionId);
        savedState!.State.Should().Be("active");

        _mockRepo.Verify(
            x => x.ClearHysteresisAsync(excursionId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Hysteresis_FalseEvaluation_BeforeExpiry_NoTransition()
    {
        SetupRule(); // hysteresis_minutes = 5
        var excursionId = Guid.NewGuid();
        var hysteresisStart = _timeProvider.GetUtcNow().UtcDateTime;

        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = excursionId,
            UpdatedAt = hysteresisStart,
        });
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Advance 3 minutes (still within 5 min hysteresis)
        _timeProvider.Advance(TimeSpan.FromMinutes(3));

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);
    }

    [Fact]
    public async Task Hysteresis_FalseEvaluation_AfterExpiry_ClosesExcursion()
    {
        SetupRule(); // hysteresis_minutes = 5
        var excursionId = Guid.NewGuid();
        var hysteresisStart = _timeProvider.GetUtcNow().UtcDateTime;

        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = excursionId,
            UpdatedAt = hysteresisStart,
        });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        // Advance past hysteresis expiry
        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        result.ExcursionId.Should().Be(excursionId);
        savedState!.State.Should().Be("idle");
        savedState.ActiveExcursionId.Should().BeNull();
        savedState.ConfirmationCount.Should().Be(0);

        _mockRepo.Verify(
            x => x.CloseExcursionAsync(excursionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Edge cases

    [Fact]
    public async Task MissingRule_ReturnsNone()
    {
        _mockRepo.Setup(x => x.GetRuleAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AlertRule?)null);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);
    }

    [Fact]
    public async Task ExcursionOpened_ReturnsNewExcursionId()
    {
        var rule = new AlertRule
        {
            Id = _ruleId,
            Name = "Quick Rule",
            ConfirmationReadings = 1,
            HysteresisMinutes = 5,
        };
        _mockRepo.Setup(x => x.GetRuleAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rule);
        SetupTrackerState(null);

        var newExcursionId = Guid.NewGuid();
        _mockRepo.Setup(x => x.CreateExcursionAsync(_ruleId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertExcursion { Id = newExcursionId, AlertRuleId = _ruleId });
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        result.ExcursionId.Should().Be(newExcursionId);
    }

    [Fact]
    public async Task ExcursionClosed_ReturnsExcursionId()
    {
        SetupRule(); // hysteresis_minutes = 5
        var excursionId = Guid.NewGuid();
        var hysteresisStart = _timeProvider.GetUtcNow().UtcDateTime;

        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = excursionId,
            UpdatedAt = hysteresisStart,
        });
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(x => x.CloseExcursionAsync(excursionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        result.ExcursionId.Should().Be(excursionId);
    }

    [Fact]
    public async Task FullLifecycle_IdleToActiveToClosedViaHysteresis()
    {
        var rule = new AlertRule
        {
            Id = _ruleId,
            Name = "Lifecycle Rule",
            ConfirmationReadings = 2,
            HysteresisMinutes = 3,
        };
        _mockRepo.Setup(x => x.GetRuleAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rule);
        SetupTrackerState(null);

        // Track state across calls
        AlertTrackerState? currentState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) =>
            {
                currentState = s;
                _mockRepo.Setup(x => x.GetTrackerStateAsync(_ruleId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(s);
            })
            .Returns(Task.CompletedTask);

        var excursionId = Guid.NewGuid();
        _mockRepo.Setup(x => x.CreateExcursionAsync(_ruleId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertExcursion { Id = excursionId, AlertRuleId = _ruleId });

        // 1. idle -> confirming (first true)
        var r1 = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);
        r1.Type.Should().Be(ExcursionTransitionType.None);
        currentState!.State.Should().Be("confirming");

        // 2. confirming -> active (second true, reaches threshold)
        var r2 = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);
        r2.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);

        // 3. active -> active (true continues)
        var r3 = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);
        r3.Type.Should().Be(ExcursionTransitionType.ExcursionContinues);

        // 4. active -> hysteresis (false)
        var r4 = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);
        r4.Type.Should().Be(ExcursionTransitionType.HysteresisStarted);

        // 5. hysteresis -> active (true before expiry)
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        var r5 = await _tracker.ProcessEvaluationAsync(_ruleId, true, null, CancellationToken.None);
        r5.Type.Should().Be(ExcursionTransitionType.HysteresisResumed);

        // 6. active -> hysteresis again (false)
        var r6 = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);
        r6.Type.Should().Be(ExcursionTransitionType.HysteresisStarted);

        // 7. hysteresis -> idle (false after expiry)
        _timeProvider.Advance(TimeSpan.FromMinutes(4));
        var r7 = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);
        r7.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        currentState!.State.Should().Be("idle");
    }

    #endregion

    #region Hysteresis window

    /// <summary>
    /// Wires the mock repository to hand back whatever the tracker last persisted, so a sequence
    /// of calls sees the state a real repository would.
    /// </summary>
    private void UseStatefulRepository(int hysteresisMinutes, AlertTrackerState? initial = null)
    {
        SetupRule(new AlertRule
        {
            Id = _ruleId,
            Name = "Window Rule",
            ConfirmationReadings = 1,
            HysteresisMinutes = hysteresisMinutes,
        });
        SetupTrackerState(initial);
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => SetupTrackerState(s))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(x => x.CreateExcursionAsync(_ruleId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertExcursion { Id = Guid.NewGuid(), AlertRuleId = _ruleId });
    }

    private async Task<ExcursionTransition> EvaluateAt(int minute, bool met, bool? autoResolveMet = null)
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(minute));
        return await _tracker.ProcessEvaluationAsync(
            _ruleId, met, autoResolveMet is { } resolve ? _ => Task.FromResult(resolve) : null,
            CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(5, 10)]
    [InlineData(12, 20)]
    [InlineData(30, 35)]
    public async Task HysteresisWindow_RunsFromEntry_ClosesOnFirstFalseEvaluationPastIt(
        int hysteresisMinutes, int closeMinute)
    {
        UseStatefulRepository(hysteresisMinutes);
        (await EvaluateAt(0, true)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        (await EvaluateAt(5, false)).Type.Should().Be(ExcursionTransitionType.HysteresisStarted);

        for (var minute = 10; minute < closeMinute; minute += 5)
        {
            (await EvaluateAt(minute, false)).Type.Should().Be(ExcursionTransitionType.None, $"minute {minute}");
        }

        var close = await EvaluateAt(closeMinute, false);
        close.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        close.CloseReason.Should().Be(ExcursionCloseReason.Hysteresis);
    }

    [Fact]
    public async Task HysteresisEntry_RecordsItsStart_AndLaterEvaluationsKeepIt()
    {
        UseStatefulRepository(30);
        await EvaluateAt(0, true);
        await EvaluateAt(5, false);
        await EvaluateAt(10, false);

        var state = await _mockRepo.Object.GetTrackerStateAsync(_ruleId);
        state!.HysteresisStartedAt.Should().Be(new DateTime(2026, 3, 22, 12, 5, 0, DateTimeKind.Utc));
        state.UpdatedAt.Should().Be(new DateTime(2026, 3, 22, 12, 5, 0, DateTimeKind.Utc),
            "an evaluation that changes nothing but the timestamp is not written");
    }

    [Fact]
    public async Task HysteresisReentry_ResumesSameExcursion_AndRestartsTheWindow()
    {
        UseStatefulRepository(30);
        var opened = await EvaluateAt(0, true);
        await EvaluateAt(5, false);

        var resumed = await EvaluateAt(20, true);
        resumed.Type.Should().Be(ExcursionTransitionType.HysteresisResumed);
        resumed.ExcursionId.Should().Be(opened.ExcursionId);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.HysteresisStartedAt.Should().BeNull();

        (await EvaluateAt(25, false)).Type.Should().Be(ExcursionTransitionType.HysteresisStarted);
        (await EvaluateAt(35, false)).Type.Should().Be(ExcursionTransitionType.None,
            "30 minutes after the first entry is only 10 after the second");
        var closed = await EvaluateAt(55, false);
        closed.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        closed.ExcursionId.Should().Be(opened.ExcursionId);
    }

    [Fact]
    public async Task HysteresisWithoutStart_AdoptsUpdatedAtOnce()
    {
        UseStatefulRepository(30, new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = Guid.NewGuid(),
            UpdatedAt = new DateTime(2026, 3, 22, 12, 5, 0, DateTimeKind.Utc),
        });

        (await EvaluateAt(10, false)).Type.Should().Be(ExcursionTransitionType.None);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.HysteresisStartedAt
            .Should().Be(new DateTime(2026, 3, 22, 12, 5, 0, DateTimeKind.Utc));
        (await EvaluateAt(30, false)).Type.Should().Be(ExcursionTransitionType.None);
        (await EvaluateAt(35, false)).Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
    }

    #endregion

    #region CloseElapsedHysteresisAsync

    private async Task<ExcursionTransition> SweepAt(int minute)
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(minute));
        return await _tracker.CloseElapsedHysteresisAsync(_ruleId, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 10)]
    [InlineData(30, 35)]
    public async Task CloseElapsedHysteresis_ClosesOnlyOnceTheWindowHasElapsed(int hysteresisMinutes, int closeMinute)
    {
        UseStatefulRepository(hysteresisMinutes);
        var opened = await EvaluateAt(0, true);
        await EvaluateAt(5, false);

        if (closeMinute > 5)
        {
            (await SweepAt(closeMinute - 1)).Type.Should().Be(ExcursionTransitionType.None);
            _mockRepo.Verify(
                x => x.CloseExcursionAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        var closed = await SweepAt(closeMinute);
        closed.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        closed.ExcursionId.Should().Be(opened.ExcursionId);
        closed.CloseReason.Should().Be(ExcursionCloseReason.Hysteresis);

        var state = await _mockRepo.Object.GetTrackerStateAsync(_ruleId);
        state!.State.Should().Be("idle");
        state.ActiveExcursionId.Should().BeNull();
        state.HysteresisStartedAt.Should().BeNull();
        _mockRepo.Verify(
            x => x.CloseExcursionAsync(opened.ExcursionId!.Value, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CloseElapsedHysteresis_IgnoresAnActiveExcursion()
    {
        UseStatefulRepository(0);
        await EvaluateAt(0, true);

        (await SweepAt(60)).Type.Should().Be(ExcursionTransitionType.None);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.State.Should().Be("active");
    }

    [Fact]
    public async Task CloseElapsedHysteresis_WithoutStart_PersistsTheAdoptedStart()
    {
        UseStatefulRepository(30, new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = Guid.NewGuid(),
            UpdatedAt = new DateTime(2026, 3, 22, 12, 5, 0, DateTimeKind.Utc),
        });

        (await SweepAt(10)).Type.Should().Be(ExcursionTransitionType.None);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.HysteresisStartedAt
            .Should().Be(new DateTime(2026, 3, 22, 12, 5, 0, DateTimeKind.Utc));
        (await SweepAt(35)).Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
    }

    #endregion

    #region Re-arm after auto-resolve

    private async Task<ExcursionTransition> ForceCloseAt(int minute, ExcursionCloseReason reason)
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(minute));
        return await _tracker.ForceCloseAsync(_ruleId, reason, CancellationToken.None);
    }

    [Fact]
    public async Task AutoResolve_OfAnActiveExcursion_OpensNothingUntilTheConditionIsFalse()
    {
        UseStatefulRepository(0);
        (await EvaluateAt(0, true)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        (await ForceCloseAt(0, ExcursionCloseReason.AutoResolve)).Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.AwaitingRearm.Should().BeTrue();

        for (var minute = 1; minute <= 3; minute++)
            (await EvaluateAt(minute, true, autoResolveMet: true)).Type.Should().Be(ExcursionTransitionType.None, $"minute {minute}");

        (await EvaluateAt(4, false, autoResolveMet: true)).Type.Should().Be(ExcursionTransitionType.None);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.AwaitingRearm.Should().BeFalse();
        (await EvaluateAt(5, true, autoResolveMet: true)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        _mockRepo.Verify(
            x => x.CreateExcursionAsync(_ruleId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task AutoResolve_OfAnActiveExcursion_ReopensAtOnceWhenTheResolveTreeGoesFalse()
    {
        UseStatefulRepository(0);
        await EvaluateAt(0, true);
        await ForceCloseAt(0, ExcursionCloseReason.AutoResolve);
        (await EvaluateAt(1, true, autoResolveMet: true)).Type.Should().Be(ExcursionTransitionType.None);

        (await EvaluateAt(2, true, autoResolveMet: false)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.AwaitingRearm.Should().BeFalse();
    }

    [Fact]
    public async Task AutoResolve_IsReadOnlyWhileAwaitingRearm()
    {
        UseStatefulRepository(0);
        var reads = 0;
        Task<bool> Resolve(CancellationToken _)
        {
            reads++;
            return Task.FromResult(true);
        }

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await _tracker.ProcessEvaluationAsync(_ruleId, true, Resolve, CancellationToken.None);
        reads.Should().Be(0, "an armed rule");
        await ForceCloseAt(0, ExcursionCloseReason.AutoResolve);

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 22, 12, 1, 0, TimeSpan.Zero));
        await _tracker.ProcessEvaluationAsync(_ruleId, true, Resolve, CancellationToken.None);
        reads.Should().Be(1);
    }

    [Theory]
    [InlineData(true, ExcursionCloseReason.Manual)]
    [InlineData(false, ExcursionCloseReason.AutoResolve)]
    public async Task OtherCloses_LeaveTheRuleArmed(bool active, ExcursionCloseReason reason)
    {
        UseStatefulRepository(60);
        await EvaluateAt(0, true);
        if (!active)
            (await EvaluateAt(1, false)).Type.Should().Be(ExcursionTransitionType.HysteresisStarted);

        (await ForceCloseAt(2, reason)).Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        (await _mockRepo.Object.GetTrackerStateAsync(_ruleId))!.AwaitingRearm.Should().BeFalse();
        (await EvaluateAt(3, true)).Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
    }

    #endregion

    #region ForceCloseAsync

    [Fact]
    public async Task ForceClose_FromActive_ClosesExcursionAndResetsToIdle()
    {
        var excursionId = Guid.NewGuid();
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "active",
            ActiveExcursionId = excursionId,
            ConfirmationCount = 0,
        });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ForceCloseAsync(_ruleId, ExcursionCloseReason.AutoResolve, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        result.ExcursionId.Should().Be(excursionId);
        result.CloseReason.Should().Be(ExcursionCloseReason.AutoResolve);

        savedState!.State.Should().Be("idle");
        savedState.ActiveExcursionId.Should().BeNull();
        savedState.ConfirmationCount.Should().Be(0);

        _mockRepo.Verify(
            x => x.CloseExcursionAsync(excursionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ForceClose_FromHysteresis_ClosesExcursionAndResetsToIdle()
    {
        var excursionId = Guid.NewGuid();
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = excursionId,
        });

        AlertTrackerState? savedState = null;
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Callback<AlertTrackerState, CancellationToken>((s, _) => savedState = s)
            .Returns(Task.CompletedTask);

        var result = await _tracker.ForceCloseAsync(_ruleId, ExcursionCloseReason.Manual, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        result.ExcursionId.Should().Be(excursionId);
        result.CloseReason.Should().Be(ExcursionCloseReason.Manual);
        savedState!.State.Should().Be("idle");

        _mockRepo.Verify(
            x => x.CloseExcursionAsync(excursionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ForceClose_FromIdle_NoOp()
    {
        SetupTrackerState(null);

        var result = await _tracker.ForceCloseAsync(_ruleId, ExcursionCloseReason.AutoResolve, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);
        result.ExcursionId.Should().BeNull();
        result.CloseReason.Should().BeNull();

        _mockRepo.Verify(
            x => x.CloseExcursionAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockRepo.Verify(
            x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ForceClose_FromConfirming_NoOp()
    {
        // confirming has no excursion id yet
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "confirming",
            ConfirmationCount = 2,
            ActiveExcursionId = null,
        });

        var result = await _tracker.ForceCloseAsync(_ruleId, ExcursionCloseReason.RuleDisabled, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.None);

        _mockRepo.Verify(
            x => x.CloseExcursionAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HysteresisExpiry_Close_CarriesHysteresisReason()
    {
        SetupRule(); // hysteresis_minutes = 5
        var excursionId = Guid.NewGuid();
        var hysteresisStart = _timeProvider.GetUtcNow().UtcDateTime;

        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = excursionId,
            UpdatedAt = hysteresisStart,
        });
        _mockRepo.Setup(x => x.UpsertTrackerStateAsync(It.IsAny<AlertTrackerState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, null, CancellationToken.None);

        result.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        result.CloseReason.Should().Be(ExcursionCloseReason.Hysteresis);
    }

    #endregion

    #region GetActiveExcursionIdAsync

    [Fact]
    public async Task GetActiveExcursionId_FromActive_ReturnsId()
    {
        var excursionId = Guid.NewGuid();
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "active",
            ActiveExcursionId = excursionId,
        });

        var result = await _tracker.GetActiveExcursionIdAsync(_ruleId, CancellationToken.None);

        result.Should().Be(excursionId);
    }

    [Fact]
    public async Task GetActiveExcursionId_FromHysteresis_ReturnsId()
    {
        var excursionId = Guid.NewGuid();
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "hysteresis",
            ActiveExcursionId = excursionId,
        });

        var result = await _tracker.GetActiveExcursionIdAsync(_ruleId, CancellationToken.None);

        result.Should().Be(excursionId);
    }

    [Fact]
    public async Task GetActiveExcursionId_FromIdle_ReturnsNull()
    {
        SetupTrackerState(null);

        var result = await _tracker.GetActiveExcursionIdAsync(_ruleId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveExcursionId_FromConfirming_ReturnsNull()
    {
        SetupTrackerState(new AlertTrackerState
        {
            AlertRuleId = _ruleId,
            State = "confirming",
            ConfirmationCount = 2,
        });

        var result = await _tracker.GetActiveExcursionIdAsync(_ruleId, CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion
}
