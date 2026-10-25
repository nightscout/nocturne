using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Rule bodies the replay corpus cannot express: the replay skips them.
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

    private static async Task<AlertReplayRun> ReplayAsync(string conditionParams)
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
        return await ReplayScenarioRunner.Rust().ReplayAsync(new AlertReplayInput([rule], [tick], IncludeTicks: true), CancellationToken.None);
    }

    [NativeTheory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task A_body_that_does_not_parse_is_skipped(string conditionParams)
    {
        var run = await ReplayAsync(conditionParams);

        run.Events.Should().BeEmpty();
        run.Ticks![0].Rules.Should().ContainSingle().Which.Skipped.Should().BeTrue();
    }
}
