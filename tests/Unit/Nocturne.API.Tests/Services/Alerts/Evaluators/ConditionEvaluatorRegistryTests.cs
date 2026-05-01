using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class ConditionEvaluatorRegistryTests
{
    private static SensorContext MakeContext(decimal? latestValue = 100m) => new()
    {
        LatestValue = latestValue,
        LatestTimestamp = DateTime.UtcNow,
        TrendRate = 0m,
        LastReadingAt = DateTime.UtcNow,
        Predictions = Array.Empty<PredictedGlucosePoint>(),
        ActiveAlerts = new Dictionary<Guid, ActiveAlertSnapshot>(),
        CurrentPath = string.Empty,
    };

    [Fact]
    public async Task EvaluateNodeAsync_KnownKind_DispatchesToMatchingEvaluator()
    {
        var registry = new ConditionEvaluatorRegistry(new IConditionEvaluator[] { new ThresholdEvaluator() });

        var node = new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70m));

        var result = await registry.EvaluateNodeAsync(node, MakeContext(latestValue: 65m), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateNodeAsync_UnknownKind_ReturnsFalse()
    {
        var registry = new ConditionEvaluatorRegistry(Array.Empty<IConditionEvaluator>());

        var node = new ConditionNode("threshold");

        var result = await registry.EvaluateNodeAsync(node, MakeContext(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public void All_non_wrapper_condition_types_have_registered_evaluator()
    {
        // Build a registry containing every IConditionEvaluator the production DI registers,
        // then assert that GetEvaluator returns non-null for every leaf AlertConditionType.
        // The recursive wrappers (Composite/Not/Sustained) and the legacy SignalLoss are
        // intentionally excluded — they don't have leaf evaluators in the same sense.
        // Drive the registration off the same extension method production uses so this
        // test fails the moment a new evaluator ships without a corresponding DI entry.
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(
            new DateTimeOffset(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc))));
        services.AddSingleton(Mock.Of<IConditionTimerStore>());

        services.AddAlertEvaluators();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var registry = new ConditionEvaluatorRegistry(
            scope.ServiceProvider.GetServices<IConditionEvaluator>());

        var nonWrapper = Enum.GetValues<AlertConditionType>()
            .Where(t => t is not (
                AlertConditionType.Composite
                or AlertConditionType.Not
                or AlertConditionType.Sustained
                or AlertConditionType.SignalLoss));

        foreach (var t in nonWrapper)
        {
            registry.GetEvaluator(t).Should().NotBeNull($"missing evaluator for {t}");
        }
    }

    [Fact]
    public async Task EvaluateNodeAsync_SerialisesKindSpecificPayload()
    {
        var captured = new List<string>();
        var stub = new Mock<IConditionEvaluator>();
        stub.SetupGet(x => x.ConditionType).Returns(AlertConditionType.Threshold);
        stub.Setup(x => x.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .Callback<string, SensorContext, CancellationToken>((json, _, _) => captured.Add(json))
            .ReturnsAsync(false);

        var registry = new ConditionEvaluatorRegistry(new[] { stub.Object });

        var node = new ConditionNode("threshold", Threshold: new ThresholdCondition("above", 250m));

        await registry.EvaluateNodeAsync(node, MakeContext(), CancellationToken.None);

        captured.Should().ContainSingle();
        captured[0].Should().Contain("\"direction\":\"above\"");
        captured[0].Should().Contain("\"value\":250");
    }
}
