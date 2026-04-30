using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class PredictedEvaluatorTests
{
    private readonly PredictedEvaluator _sut = new();

    [Fact]
    public void ConditionType_ShouldBePredicted()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.Predicted);
    }

    [Fact]
    public async Task EmptyPredictions_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 180, "within_minutes": 30}""";
        var context = MakeContext(Array.Empty<PredictedGlucosePoint>());

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task MatchWithinHorizon_ReturnsTrue()
    {
        var json = """{"operator": ">", "value": 180, "within_minutes": 30}""";
        var context = MakeContext(new[]
        {
            new PredictedGlucosePoint(15, 200m), // matches and within horizon
        });

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task MatchOutsideHorizon_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 180, "within_minutes": 30}""";
        var context = MakeContext(new[]
        {
            new PredictedGlucosePoint(45, 200m), // matches but past horizon
        });

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NoMatchWithinHorizon_ReturnsFalse()
    {
        var json = """{"operator": ">", "value": 180, "within_minutes": 30}""";
        var context = MakeContext(new[]
        {
            new PredictedGlucosePoint(10, 150m),
            new PredictedGlucosePoint(20, 170m),
        });

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AnyMatchInHorizonShortCircuits()
    {
        var json = """{"operator": "<", "value": 70, "within_minutes": 30}""";
        var context = MakeContext(new[]
        {
            new PredictedGlucosePoint(5, 100m),
            new PredictedGlucosePoint(15, 60m),  // matches
            new PredictedGlucosePoint(25, 110m),
        });

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    private static SensorContext MakeContext(IReadOnlyList<PredictedGlucosePoint> predictions) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        Predictions = predictions
    };
}
