using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// Keeps over time, polled with the clock moving: a failed keep is read again only once its life is over, a live keep
/// read at the <c>/overview</c> feed's cadence is read again no sooner than its refresh point, a page that polls once a
/// minute never finds it missing, and a live fact writes one read-audit row per viewer per
/// <see cref="FlagFactEvaluator.PeriodKeep"/>.
/// </summary>
public partial class FlagFactEvaluatorTests
{
    [Fact]
    public async Task A_failed_live_keep_is_read_again_only_once_its_life_is_over()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromMinutes(10), 12.34, 40)]), Devices);
        harness.PumpFailsFor = person.Tenant.Id;
        var rule = Rule("device_status", "sensorAgeHours", null, FlagRuleComparison.Above, 240);

        await FailedKeepIsReadAgainOnlyAfterItsLifeAsync(harness, person, rule, ReportFlagFactKind.Live, () => harness.PumpReads);
    }

    [Fact]
    public async Task A_failed_period_keep_is_read_again_only_once_its_life_is_over()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), Defect: new InvalidOperationException("a defect")));
        var rule = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4);

        await FailedKeepIsReadAgainOnlyAfterItsLifeAsync(harness, person, rule, ReportFlagFactKind.Period, () => harness.GlucoseReads);
    }

    [Fact]
    public async Task A_live_keep_read_at_the_overview_feeds_cadence_is_read_again_no_sooner_than_its_refresh_point()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromMinutes(10), 12.34, 40)]), Devices);
        IReadOnlyList<FlagRule> rules = [Rule("device_status", "sensorAgeHours", null, FlagRuleComparison.Above, 240)];
        var span = TimeSpan.FromMinutes(10);

        var pending = await PollAsync(harness, rules, person, TimeSpan.FromSeconds(5), span);

        var soonestRefresh = FlagFactEvaluator.LiveKeep * (1 - FlagFactEvaluator.KeepSpread) * FlagFactEvaluator.RefreshAfter;
        output.WriteLine($"{pending.Count} reads 5 s apart over {span}: {harness.PumpReads} background reads.");
        harness.PumpReads.Should().BeLessThanOrEqualTo(1 + (int)(span / soonestRefresh),
            "a keep is read again no sooner than its refresh point, however often the page reads");
        pending.Skip(1).Should().OnlyContain(p => p == 0);
    }

    [Fact]
    public async Task A_page_polling_once_a_minute_never_finds_a_live_keep_missing_after_the_first_read()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromMinutes(10), 12.34, 40)]), Devices);
        IReadOnlyList<FlagRule> rules = [Rule("device_status", "sensorAgeHours", null, FlagRuleComparison.Above, 240)];

        var pending = await PollAsync(harness, rules, person, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));

        pending[0].Should().Be(1);
        pending.Skip(1).Should().OnlyContain(p => p == 0, "the rest of the shortest live life after its refresh point is longer than a minute");
        harness.PumpReads.Should().BeGreaterThan(1, "the keep was read again behind the polls over several lives");
    }

    [Fact]
    public async Task A_live_fact_writes_one_read_audit_row_per_viewer_per_quarter_hour_whether_kept_or_judged_on_the_snapshot()
    {
        var kept = new Harness();
        var person = kept.Follow(new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromMinutes(10), 12.34, 40)]), Devices);
        var other = Guid.NewGuid();
        var otherEvaluator = kept.For(other);
        IReadOnlyList<FlagRule> sensorAge = [Rule("device_status", "sensorAgeHours", null, FlagRuleComparison.Above, 240)];
        var snapshot = new Harness();
        var pumped = snapshot.Follow(new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromMinutes(10), 12.34, 40)]), Devices);
        IReadOnlyList<FlagRule> reservoir = [Rule("device_status", "reservoir", null, FlagRuleComparison.Below, 20)];

        for (var pass = 0; pass < 10; pass++)
        {
            if (pass > 0)
            {
                kept.Time.Advance(TimeSpan.FromMinutes(1));
                snapshot.Time.Advance(TimeSpan.FromMinutes(1));
            }
            await RulesPassAsync(kept, sensorAge, [person]);
            await RulesPassAsync(kept, otherEvaluator, sensorAge, [person], credentialLimitTo24Hours: false);
            await kept.Reads.WhenIdleAsync();
            await RulesPassAsync(snapshot, reservoir, [pumped]);
            await snapshot.Reads.WhenIdleAsync();
        }

        kept.PumpReads.Should().BeGreaterThan(1, "the live keep was read again within the ten minutes");
        var keptRows = await kept.AuditRowsAsync();
        keptRows.Where(r => r.ActorSubjectId == Viewer).Should().ContainSingle("a kept live fact is audited once per quarter hour");
        keptRows.Where(r => r.ActorSubjectId == other).Should().ContainSingle("each viewer writes their own row");
        keptRows.Should().HaveCount(2);
        (await snapshot.AuditRowsAsync()).Should().ContainSingle("a live fact judged on the snapshot is audited once per quarter hour");
    }

    /// <summary>
    /// Polls <paramref name="rule"/> for <paramref name="person"/> once a minute, each pass followed by its background
    /// reads, until past the longest keep of <paramref name="kind"/>, and asserts the failed keep is read once and
    /// logged once until its life is over, then pending for that one pass and read exactly once more.
    /// </summary>
    private static async Task FailedKeepIsReadAgainOnlyAfterItsLifeAsync(
        Harness harness, GlucoseReadTenant person, FlagRule rule, ReportFlagFactKind kind, Func<int> reads)
    {
        var condition = harness.Evaluator.CheckRule(rule.Dataset, rule.Field, rule.Days, rule.Comparison, rule.Value).Condition!;
        var life = FlagFactEvaluator.KeepLife(person.Tenant.Id, condition.ReadKey, kind);
        var longest = kind == ReportFlagFactKind.Live ? FlagFactEvaluator.LiveKeep : FlagFactEvaluator.PeriodKeep;
        var minutes = (int)Math.Ceiling(longest.TotalMinutes) + 1;

        var seen = new List<(int Minute, int Pending, int Reads, int Errors)>();
        for (var minute = 0; minute <= minutes; minute++)
        {
            if (minute > 0)
                harness.Time.Advance(TimeSpan.FromMinutes(1));
            var (reasons, pending) = await RulesPassAsync(harness, [rule], [person]);
            await harness.Reads.WhenIdleAsync();
            reasons.Single().Should().BeEmpty();
            seen.Add((minute, pending, reads(), harness.Errors.Count));
        }

        var again = Enumerable.Range(1, minutes).First(m => TimeSpan.FromMinutes(m) >= life);
        var expected = seen.Select(s => s.Minute == 0 ? (s.Minute, 1, 1, 1)
            : s.Minute < again ? (s.Minute, 0, 1, 1)
            : s.Minute == again ? (s.Minute, 1, 2, 2)
            : (s.Minute, 0, 2, 2));
        seen.Should().Equal(expected, $"a failed keep of life {life} is read again only once that life is over");
    }

    /// <summary>
    /// The people pending on each pass of <paramref name="rules"/> for <paramref name="person"/>, one pass each
    /// <paramref name="every"/> over <paramref name="span"/>, each followed by its background reads.
    /// </summary>
    private static async Task<IReadOnlyList<int>> PollAsync(
        Harness harness, IReadOnlyList<FlagRule> rules, GlucoseReadTenant person, TimeSpan every, TimeSpan span)
    {
        var pending = new List<int>();
        for (var at = TimeSpan.Zero; at <= span; at += every)
        {
            if (at > TimeSpan.Zero)
                harness.Time.Advance(every);
            var (_, waiting) = await RulesPassAsync(harness, rules, [person]);
            await harness.Reads.WhenIdleAsync();
            pending.Add(waiting);
        }
        return pending;
    }
}
