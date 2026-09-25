using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

[Trait("Category", "Unit")]
public class ManagedAlertEngineTests
{
    private readonly ListLogger<ManagedAlertEngine> _logger = new();

    private ManagedAlertEngine EngineWithoutEvaluators() => new(
        new ConditionEvaluatorRegistry([]),
        new ExcursionTracker(
            Mock.Of<IAlertTrackerRepository>(), new AlertRuleEvaluationGate(), TimeProvider.System,
            NullLogger<ExcursionTracker>.Instance),
        new ConditionVersionLog(),
        _logger);

    private static readonly SensorContext NoReading = new()
    {
        LatestValue = null,
        LatestTimestamp = null,
        TrendRate = null,
        LastReadingAt = null,
    };

    private static AlertRuleSnapshot Rule(Guid id, string conditionParams) =>
        new(id, Guid.Empty, "rule", AlertConditionType.Threshold, conditionParams, AlertRuleSeverity.Warning,
            "{}", 0, AutoResolveEnabled: false, AutoResolveParams: null);

    [Fact]
    public async Task A_rule_with_no_evaluator_is_reported_once_per_condition_version()
    {
        var engine = EngineWithoutEvaluators();
        var id = Guid.NewGuid();
        var low = Rule(id, """{"direction":"below","value":70}""");

        for (var i = 0; i < 3; i++)
        {
            var result = await engine.EvaluateRuleAsync(low, NoReading, new AlertEngineOptions(), default);
            result.Skipped.Should().BeTrue();
        }
        _logger.Warnings.Should().ContainSingle();

        await engine.EvaluateRuleAsync(
            low with { ConditionParams = """{"direction":"below","value":60}""" },
            NoReading, new AlertEngineOptions(), default);
        _logger.Warnings.Should().HaveCount(2);
    }
}
