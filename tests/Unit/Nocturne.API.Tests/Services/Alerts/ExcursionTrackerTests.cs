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
        _mockRepo = new Mock<IAlertTrackerRepository>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));

        var logger = new Mock<ILogger<ExcursionTracker>>();
        _tracker = new ExcursionTracker(_mockRepo.Object, _timeProvider, logger.Object);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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
        var r1 = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);
        r1.Type.Should().Be(ExcursionTransitionType.None);
        savedStates[0].ConfirmationCount.Should().Be(1);

        // Call 2: confirming -> confirming (count=2)
        var r2 = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);
        r2.Type.Should().Be(ExcursionTransitionType.None);
        savedStates[1].ConfirmationCount.Should().Be(2);

        // Call 3: confirming -> active (count reaches 3)
        var r3 = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);
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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);

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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);

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
        var r1 = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);
        r1.Type.Should().Be(ExcursionTransitionType.None);
        currentState!.State.Should().Be("confirming");

        // 2. confirming -> active (second true, reaches threshold)
        var r2 = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);
        r2.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);

        // 3. active -> active (true continues)
        var r3 = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);
        r3.Type.Should().Be(ExcursionTransitionType.ExcursionContinues);

        // 4. active -> hysteresis (false)
        var r4 = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);
        r4.Type.Should().Be(ExcursionTransitionType.HysteresisStarted);

        // 5. hysteresis -> active (true before expiry)
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        var r5 = await _tracker.ProcessEvaluationAsync(_ruleId, true, CancellationToken.None);
        r5.Type.Should().Be(ExcursionTransitionType.HysteresisResumed);

        // 6. active -> hysteresis again (false)
        var r6 = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);
        r6.Type.Should().Be(ExcursionTransitionType.HysteresisStarted);

        // 7. hysteresis -> idle (false after expiry)
        _timeProvider.Advance(TimeSpan.FromMinutes(4));
        var r7 = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);
        r7.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        currentState!.State.Should().Be("idle");
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

        var result = await _tracker.ProcessEvaluationAsync(_ruleId, false, CancellationToken.None);

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
