using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

[Trait("Category", "Unit")]
public class ManagedAlertReplayEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private const string Low70 = """{"type":"threshold","threshold":{"direction":"below","value":70}}""";
    private const string NoDirection = """{"type":"threshold","threshold":{"value":70}}""";

    private readonly ManagedAlertReplayEngine _engine = new(NullLogger<ManagedAlertReplayEngine>.Instance);

    private static AlertRuleSnapshot Rule(
        Guid id, AlertConditionType type, string conditionParams, string? autoResolveParams = null) =>
        new(id, Guid.Empty, "rule", type, conditionParams, AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: autoResolveParams is not null, AutoResolveParams: autoResolveParams);

    private static AlertReplayTick Reading(int minutes, decimal mgdl) => Tick(minutes, new SensorContext
    {
        LatestValue = mgdl,
        LatestTimestamp = T0.AddMinutes(minutes),
        TrendRate = 0m,
        LastReadingAt = T0.AddMinutes(minutes),
    });

    private static AlertReplayTick Tick(int minutes, SensorContext context) =>
        new(T0.AddMinutes(minutes), context, new HashSet<Guid>());

    [Fact]
    public async Task ARuleThatCannotBeEvaluated_IsSkipped_EvenWhereTheEvaluationOrderWouldNotReachTheFault()
    {
        var ruleId = Guid.NewGuid();
        var rule = Rule(ruleId, AlertConditionType.Composite,
            """{"operator":"or","conditions":[""" + Low70 + "," + NoDirection + "]}");

        var run = await _engine.ReplayAsync(
            new AlertReplayInput([rule], [Reading(0, 60m)], IncludeTicks: true), CancellationToken.None);

        run.Events.Should().BeEmpty();
        run.LeafTransitions.Should().BeEmpty();
        run.Ticks![0].Rules.Should().ContainSingle().Which.Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task AnAutoResolveTreeThatCannotBeEvaluated_NeverResolves()
    {
        var ruleId = Guid.NewGuid();
        var rule = Rule(ruleId, AlertConditionType.Threshold, """{"direction":"below","value":70}""",
            autoResolveParams: """{"type":"composite","composite":{"operator":"or","conditions":["""
                + Low70 + "," + NoDirection + "]}}");

        var run = await _engine.ReplayAsync(
            new AlertReplayInput([rule], [Reading(0, 60m), Reading(5, 60m)]), CancellationToken.None);

        run.Events.Should().ContainSingle().Which.Kind.Should().Be(AlertReplayTransition.Fired);
    }
}
