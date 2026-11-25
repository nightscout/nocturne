using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// A stored not-true instant from before this process began recording is raised to that instant
/// (<see cref="NotTrueRecordingFloor"/>, docs/alerts/engine-semantics.md §7).
/// </summary>
public class NotTrueRecordingFloorTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly DateTime Noon = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Raise_lifts_only_instants_before_the_process_began_and_keeps_every_path()
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(Noon);
        var floor = new NotTrueRecordingFloor(time);
        time.SetUtcNow(Noon.AddHours(1));

        var raised = floor.Raise(new ConditionTimers(
            new Dictionary<string, DateTime> { ["running"] = Noon.AddDays(-1) },
            new Dictionary<string, DateTime>
            {
                ["stale"] = Noon.AddDays(-1),
                ["recorded"] = Noon.AddMinutes(10),
            }));

        floor.Since.Should().Be(Noon, "the instant is captured when the floor is built, not when it is read");
        raised.FirstTrue.Should().Equal(new Dictionary<string, DateTime> { ["running"] = Noon.AddDays(-1) });
        raised.NotTrue.Should().Equal(new Dictionary<string, DateTime>
        {
            ["stale"] = Noon,
            ["recorded"] = Noon.AddMinutes(10),
        });
    }

    /// <summary>
    /// A not-true row left from yesterday, then an older image that found no override at 11:59 and
    /// recorded nothing; this process starts at 12:00 and reads an override back-dated to 11:20. A
    /// sustained{30} must not count the 40 minutes the row's age would allow: the run starts at 12:00
    /// or later and the root is false.
    /// </summary>
    [NativeFact]
    public async Task A_stale_not_true_row_cannot_start_a_run_before_this_process_began()
    {
        var rule = new AlertRule
        {
            Id = RuleId,
            Name = "override held",
            ConditionType = AlertConditionType.Sustained,
            ConditionParams =
                """{"minutes":30,"child":{"type":"override_active","override_active":{"is_active":true}}}""",
            HysteresisMinutes = 0,
        };
        var trackerRepo = new InMemoryTrackerRepository([rule]);
        await trackerRepo.Timers.SetNotTrueAsync(RuleId, "sustained", Noon.AddDays(-1), CancellationToken.None);
        trackerRepo.Timers.DrainOps();

        var time = new ManualTimeProvider();
        time.SetUtcNow(Noon);
        var engine = EngineTestHarness.BuildRustEngine(time, trackerRepo);
        var snapshot = new AlertRuleSnapshot(
            rule.Id, EngineTestHarness.TenantId, rule.Name, rule.ConditionType, rule.ConditionParams,
            AlertRuleSeverity.Warning, "{}", 0, false, null);
        var context = new SensorContext
        {
            LatestValue = 120,
            LatestTimestamp = Noon,
            TrendRate = null,
            LastReadingAt = Noon,
            ActiveOverride = new OverrideSnapshot(Noon.AddMinutes(-40)),
        };

        var evaluation = await engine.EvaluateRuleAsync(snapshot, context, AlertEngineOptions.Default, CancellationToken.None);

        evaluation.ConditionMet.Should().BeFalse("the override has held 30 minutes only by a bound older than this process");
        var start = (await trackerRepo.Timers.GetAllForRuleAsync(RuleId, CancellationToken.None)).FirstTrue["sustained"];
        start.Should().BeOnOrAfter(Noon);
    }
}
