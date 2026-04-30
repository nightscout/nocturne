using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class SustainedEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly FakeTimeProvider _timeProvider;
    private readonly Mock<IConditionTimerStore> _timerStore;
    private readonly SustainedEvaluator _sut;
    private readonly Guid _ruleId = Guid.NewGuid();

    public SustainedEvaluatorTests()
    {
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _timerStore = new Mock<IConditionTimerStore>();

        // Build a registry containing the leaf evaluators we'll use in tests, plus the SUT itself
        // (so nested-sustained tests can route correctly).
        var serviceProvider = new Mock<IServiceProvider>();

        var sustainedEval = new SustainedEvaluator(serviceProvider.Object, _timerStore.Object, _timeProvider);

        var evaluators = new IConditionEvaluator[]
        {
            new ThresholdEvaluator(),
            new RateOfChangeEvaluator(),
            sustainedEval,
        };
        var registry = new ConditionEvaluatorRegistry(evaluators);

        serviceProvider
            .Setup(sp => sp.GetService(typeof(ConditionEvaluatorRegistry)))
            .Returns(registry);

        _sut = sustainedEval;
    }

    [Fact]
    public void ConditionType_ShouldBeSustained()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Sustained);
    }

    [Fact]
    public async Task ChildFalse_ClearsTimerAndReturnsFalse()
    {
        // Threshold "below 70" with value 100 = false.
        var sustained = new SustainedCondition(
            Minutes: 10,
            Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)));
        var json = JsonSerializer.Serialize(sustained, SnakeCaseOptions);
        var context = MakeContext(latestValue: 100m, path: "sustained");

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeFalse();
        _timerStore.Verify(t => t.ClearAsync(_ruleId, "sustained", It.IsAny<CancellationToken>()), Times.Once);
        _timerStore.Verify(t => t.SetFirstTrueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChildTrueWithNoTimer_SetsTimerAndReturnsFalse()
    {
        var sustained = new SustainedCondition(
            Minutes: 10,
            Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)));
        var json = JsonSerializer.Serialize(sustained, SnakeCaseOptions);
        var context = MakeContext(latestValue: 60m, path: "sustained");

        _timerStore.Setup(t => t.GetFirstTrueAsync(_ruleId, "sustained", It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeFalse();
        _timerStore.Verify(t => t.SetFirstTrueAsync(_ruleId, "sustained", FixedNow, It.IsAny<CancellationToken>()), Times.Once);
        _timerStore.Verify(t => t.ClearAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChildTrueWithTimerNotElapsed_ReturnsFalse()
    {
        var sustained = new SustainedCondition(
            Minutes: 10,
            Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)));
        var json = JsonSerializer.Serialize(sustained, SnakeCaseOptions);
        var context = MakeContext(latestValue: 60m, path: "sustained");

        // Timer set 5 minutes ago, threshold is 10 minutes => not yet elapsed.
        _timerStore.Setup(t => t.GetFirstTrueAsync(_ruleId, "sustained", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedNow.AddMinutes(-5));

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeFalse();
        _timerStore.Verify(t => t.SetFirstTrueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _timerStore.Verify(t => t.ClearAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChildTrueWithTimerExactlyElapsed_ReturnsTrue()
    {
        var sustained = new SustainedCondition(
            Minutes: 10,
            Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)));
        var json = JsonSerializer.Serialize(sustained, SnakeCaseOptions);
        var context = MakeContext(latestValue: 60m, path: "sustained");

        // Exactly 10 minutes elapsed => >= 10 => true.
        _timerStore.Setup(t => t.GetFirstTrueAsync(_ruleId, "sustained", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedNow.AddMinutes(-10));

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ChildTrueWithTimerExceedsElapsed_ReturnsTrue()
    {
        var sustained = new SustainedCondition(
            Minutes: 10,
            Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)));
        var json = JsonSerializer.Serialize(sustained, SnakeCaseOptions);
        var context = MakeContext(latestValue: 60m, path: "sustained");

        _timerStore.Setup(t => t.GetFirstTrueAsync(_ruleId, "sustained", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedNow.AddMinutes(-30));

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task UnregisteredChildType_ReturnsFalseAndClearsTimer()
    {
        // "iob" is not in our test registry; child evaluates false (silent fail-mode),
        // which means the sustained timer is cleared.
        var sustained = new SustainedCondition(
            Minutes: 10,
            Child: new ConditionNode("iob"));
        var json = JsonSerializer.Serialize(sustained, SnakeCaseOptions);
        var context = MakeContext(latestValue: 60m, path: "sustained");

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeFalse();
        _timerStore.Verify(t => t.ClearAsync(_ruleId, "sustained", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NullChild_ReturnsFalseWithoutTouchingTimerStore()
    {
        // SustainedCondition serialized with a null Child — defensive guard.
        var json = """{"minutes": 10, "child": null}""";
        var context = MakeContext(latestValue: 60m, path: "sustained");

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeFalse();
        _timerStore.Verify(
            t => t.ClearAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _timerStore.Verify(
            t => t.SetFirstTrueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveMinutes_ReturnsFalseWithoutTouchingTimerStore(int minutes)
    {
        // Misconfigured rule: zero/negative sustained windows would make the evaluator fire
        // on the second pass with no real "sustained" semantics. Treat as non-firing so a
        // bad rule never alerts.
        var json = $$"""{"minutes": {{minutes}}, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70} } }""";
        var context = MakeContext(latestValue: 60m, path: "sustained");

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeFalse();
        _timerStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TimerKeyedByCurrentPathFromContext()
    {
        // The orchestrator may seed CurrentPath to a deeper path (e.g. when sustained is
        // wrapped inside a composite). The evaluator must use that path verbatim, not
        // hardcode "sustained".
        var deepPath = "composite[1].sustained";
        var sustained = new SustainedCondition(
            Minutes: 10,
            Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)));
        var json = JsonSerializer.Serialize(sustained, SnakeCaseOptions);
        var context = MakeContext(latestValue: 60m, path: deepPath);

        _timerStore.Setup(t => t.GetFirstTrueAsync(_ruleId, deepPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        var result = await _sut.EvaluateAsync(json, context, CancellationToken.None);

        result.Should().BeFalse();
        _timerStore.Verify(t => t.GetFirstTrueAsync(_ruleId, deepPath, It.IsAny<CancellationToken>()), Times.Once);
        _timerStore.Verify(t => t.SetFirstTrueAsync(_ruleId, deepPath, FixedNow, It.IsAny<CancellationToken>()), Times.Once);
    }

    private SensorContext MakeContext(decimal? latestValue, string path) => new()
    {
        LatestValue = latestValue,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        CurrentRuleId = _ruleId,
        CurrentPath = path,
    };
}
