using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Rule bodies the replay corpus cannot express: each engine must skip them the same way.
/// </summary>
public class RustAlertReplayEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> UnreadableBodies() => new()
    {
        "{not json",
        "",
        "{\"direction\":\"below\"",
        """["not","an","object"]""",
    };

    private static RustAlertReplayEngine Rust() => new(new AlertEngineErrors(
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
        TimeProvider.System));

    private static async Task<AlertReplayRun> ReplayAsync(IAlertReplayEngine engine, string conditionParams)
    {
        var rule = new AlertRuleSnapshot(
            Guid.NewGuid(), Guid.Empty, "rule", AlertConditionType.Threshold, conditionParams,
            AlertRuleSeverity.Warning, "{}", 0, AutoResolveEnabled: false, AutoResolveParams: null);
        var tick = new AlertReplayTick(T0, new SensorContext
        {
            LatestValue = 60m,
            LatestTimestamp = T0,
            TrendRate = 0m,
            LastReadingAt = T0,
        }, new HashSet<Guid>());
        return await engine.ReplayAsync(new AlertReplayInput([rule], [tick], IncludeTicks: true), CancellationToken.None);
    }

    [NativeTheory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task A_body_that_does_not_parse_is_skipped_by_both_engines(string conditionParams)
    {
        var managed = await ReplayAsync(new ManagedAlertReplayEngine(NullLogger<ManagedAlertReplayEngine>.Instance), conditionParams);
        var rust = await ReplayAsync(Rust(), conditionParams);

        foreach (var run in new[] { managed, rust })
        {
            run.Events.Should().BeEmpty();
            run.Ticks![0].Rules.Should().ContainSingle().Which.Skipped.Should().BeTrue();
        }
    }
}
