using FluentAssertions;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Reports;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// A viewer's own flag rules judged as the People overview judges them (<see cref="ViewerFlagRules"/>): the reason a
/// rule gives, what never flags, the first read that waits for nothing, a person whose background read fails, that no
/// request waits on the background reads' limit, the pump facts judged on the overview's own snapshot, and the read
/// audit.
/// </summary>
public partial class FlagFactEvaluatorTests
{
    [Fact]
    public async Task A_matched_rule_gives_a_reason_with_every_field_once_its_background_read_is_kept()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var rule = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, NotificationUrgency.Hazard);

        var (cold, coldPending) = await RulesPassAsync(harness, [rule], [person]);
        await harness.Reads.WhenIdleAsync();
        var (warm, warmPending) = await RulesPassAsync(harness, [rule], [person]);

        cold.Single().Should().BeEmpty("the first read waits for no figures and the person carries the fixed set");
        coldPending.Should().Be(1);
        warmPending.Should().Be(0);
        var reason = warm.Single().Should().ContainSingle().Subject;
        (reason.Kind, reason.Level).Should().Be((TenantOverviewAttentionKind.FlagRule, NotificationUrgency.Hazard));
        var shown = reason.Rule!;
        (shown.RuleId, shown.Source, shown.Dataset, shown.Field, shown.Unit, shown.Range)
            .Should().Be((rule.Id, FlagRuleSource.Yours, "glucose_statistics", "timeBelow", "%", "consensus"));
        (shown.Comparison, shown.RuleValue, shown.Days).Should().Be((FlagRuleComparison.Above, 4.0, (int?)14));
        shown.Label.Should().NotBeNullOrWhiteSpace();
        shown.Value.Should().BeApproximately(10, 1);
        shown.From.Should().Be(Start.AddDays(-14).UtcDateTime);
        shown.To.Should().Be(Start.UtcDateTime);
        shown.Bound.Should().BeNull();
        shown.Coverage.Should().BeNull("coverage is stated only when there was too little data");
    }

    [Fact]
    public async Task An_own_range_rule_states_the_bound_it_was_measured_against_and_that_it_varies_over_the_day()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), [("00:00", 70, 150), ("08:00", 90, 170)]), Therapy);

        var reasons = await RulesJudgedAsync(harness,
            [Rule("glucose_statistics", "timeBelowOwnRange", 14, FlagRuleComparison.Above, 4)], [person]);

        var shown = reasons.Single().Should().ContainSingle().Subject.Rule!;
        shown.Range.Should().Be("own");
        shown.Bound.Should().Be(new TenantOverviewRuleBound(70, 170, true));
    }

    [Fact]
    public async Task No_data_never_flags()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.Zero));

        var reasons = await RulesJudgedAsync(harness,
            [
                Rule("glucose_statistics", "sensorWornPercent", 14, FlagRuleComparison.Below, 70),
                Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 0),
                Rule("current_status", "hoursSinceReading", null, FlagRuleComparison.Above, 0),
            ],
            [person]);

        reasons.Single().Should().BeEmpty();
    }

    [Fact]
    public async Task Too_little_data_that_matches_flags_with_its_coverage_and_too_little_that_does_not_never_flags()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(2)));
        var matching = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4);

        var reasons = await RulesJudgedAsync(harness,
            [matching, Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 50)], [person]);

        var shown = reasons.Single().Should().ContainSingle().Subject.Rule!;
        shown.RuleId.Should().Be(matching.Id);
        shown.Coverage.Should().BeApproximately(100.0 * 2 / 14, 0.5);
    }

    [Fact]
    public async Task A_caller_limited_to_24_hours_gets_no_rule_reason_and_nothing_is_read()
    {
        var harness = new Harness();
        var data = new Person(Pattern, TimeSpan.FromDays(15), LatestAge: TimeSpan.FromHours(3));
        var person = harness.Follow(data);
        var limitedMember = harness.Follow(data, limitTo24Hours: true);
        IReadOnlyList<FlagRule> rules =
        [
            Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4),
            Rule("current_status", "hoursSinceReading", null, FlagRuleComparison.Above, 2),
        ];

        var (byCredential, pending) = await RulesPassAsync(harness, rules, [person], credentialLimitTo24Hours: true);
        var byMembership = await RulesJudgedAsync(harness, rules, [limitedMember]);
        await harness.Reads.WhenIdleAsync();

        byCredential.Single().Should().BeEmpty();
        pending.Should().Be(0, "a person who cannot be checked is not still being checked");
        byMembership.Single().Should().BeEmpty();
        harness.GlucoseReads.Should().Be(0);
    }

    [Fact]
    public async Task A_person_whose_background_read_fails_carries_no_rule_reason_is_logged_once_and_the_others_are_judged()
    {
        var harness = new Harness();
        var data = new Person(Pattern, TimeSpan.FromDays(1), LatestAge: TimeSpan.FromHours(3));
        var served = harness.Follow(data, Devices);
        var failing = harness.Follow(data, Devices);
        harness.PumpFailsFor = failing.Tenant.Id;
        IReadOnlyList<FlagRule> rules =
        [
            Rule("device_status", "sensorAgeHours", null, FlagRuleComparison.Above, 240),
            Rule("current_status", "hoursSinceReading", null, FlagRuleComparison.Above, 2),
        ];

        var (_, firstPending) = await RulesPassAsync(harness, rules, [served, failing]);
        await harness.Reads.WhenIdleAsync();
        var (second, secondPending) = await RulesPassAsync(harness, rules, [served, failing]);
        await harness.Reads.WhenIdleAsync();
        var (third, _) = await RulesPassAsync(harness, rules, [served, failing]);

        firstPending.Should().Be(2, "the sensor age is read in the background, never on the request");
        secondPending.Should().Be(0, "a failed read is not still being checked");
        second[0].Should().ContainSingle().Which.Rule!.Field.Should().Be("hoursSinceReading");
        second[1].Should().BeEmpty();
        third[1].Should().BeEmpty();
        harness.PumpReads.Should().Be(2, "the failure is kept, so the person is not read again on every call");
        harness.Log.Errors.Should().ContainSingle("the failure is logged once for the keep it makes");
    }

    [Fact]
    public async Task A_background_read_that_fails_with_a_defect_is_kept_as_a_failure_and_logged_once()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), Defect: new InvalidOperationException("a defect")));
        IReadOnlyList<FlagRule> rules = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];

        var pending = new List<int>();
        var reasons = new List<IReadOnlyList<TenantOverviewAttention>>();
        for (var pass = 0; pass < 3; pass++)
        {
            var (judged, waiting) = await RulesPassAsync(harness, rules, [person]);
            await harness.Reads.WhenIdleAsync();
            pending.Add(waiting);
            reasons.Add(judged.Single());
        }

        harness.GlucoseReads.Should().Be(1, "a defect is kept as a failure, so the person is not read again on every call");
        pending.Should().Equal([1, 0, 0], "a failed read is not still being checked");
        reasons.Should().OnlyContain(r => r.Count == 0);
        harness.Log.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task No_request_waits_on_the_limit_the_background_reads_hold()
    {
        var harness = new Harness(limit: 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.DuringGlucoseRead = () => gate.Task;
        var first = Enumerable.Range(0, 5).Select(_ => harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)))).ToList();
        var second = Enumerable.Range(0, 5).Select(_ => harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)))).ToList();
        var devices = harness.Follow(new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromMinutes(10), 12.34, 40)]), Devices);
        IReadOnlyList<FlagRule> rules =
        [
            Rule("device_status", "reservoir", null, FlagRuleComparison.Below, 20),
            Rule("device_status", "sensorAgeHours", null, FlagRuleComparison.Above, 240),
        ];

        await Task.WhenAll(
                Harness.JudgeOnceAsync(harness.Evaluator, first, [TimeBelow(harness)], false, CancellationToken.None),
                Harness.JudgeOnceAsync(harness.For(Guid.NewGuid()), second, [TimeBelow(harness)], false, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => harness.GlucoseReadsAtOnce == 2);
        var overview = RulesPassAsync(harness, rules, [devices]);
        var finished = await Task.WhenAny(overview, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(overview, "an overview read with live rules completes while the background reads hold every slot");
        (await overview).Pending.Should().Be(1, "the sensor age waits for its background read");
        harness.PumpReads.Should().Be(0);
        gate.SetResult();
        await harness.Reads.WhenIdleAsync();
        harness.MostGlucoseReadsAtOnce.Should().Be(2);
        harness.GlucoseReads.Should().Be(10);
        var (judged, pending) = await RulesPassAsync(harness, rules, [devices]);
        pending.Should().Be(0);
        judged.Single().Should().ContainSingle().Which.Rule!.Field.Should().Be("reservoir");
    }

    [Fact]
    public async Task Pump_rules_are_judged_on_the_overviews_own_snapshot_with_no_second_read()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromHours(2), 80, 90), Pump(TimeSpan.FromMinutes(10), 12.34, 40)]),
            Devices);
        IReadOnlyList<FlagRule> rules =
        [
            Rule("device_status", "reservoir", null, FlagRuleComparison.Below, 20),
            Rule("device_status", "pumpBattery", null, FlagRuleComparison.Below, 50),
        ];

        var (reasons, pending) = await RulesPassAsync(harness, rules, [person]);
        await harness.Reads.WhenIdleAsync();
        var pumpReads = harness.PumpReads;
        var report = await harness.ReportAsync(person, "device_status", 24);

        pending.Should().Be(0, "the snapshot the overview read decides both figures");
        pumpReads.Should().Be(0, "no second read of the pump is made, in the request or in the background");
        reasons.Single().Select(r => (r.Rule!.Field, r.Rule.Value)).Should().Equal(
            ("reservoir", Convert.ToDouble(report["reservoir"])), ("pumpBattery", Convert.ToDouble(report["pumpBattery"])));
        reasons.Single().Select(r => r.Rule!.Value).Should().Equal(12.3, 40);
    }

    [Fact]
    public async Task A_newest_snapshot_without_the_pump_figures_leaves_them_to_the_read_the_dataset_makes()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromHours(2), 12.34, 40), Pump(TimeSpan.FromMinutes(10), null, null)]),
            Devices);
        IReadOnlyList<FlagRule> rules = [Rule("device_status", "reservoir", null, FlagRuleComparison.Below, 20)];

        var (cold, coldPending) = await RulesPassAsync(harness, rules, [person]);
        await harness.Reads.WhenIdleAsync();
        var (warm, warmPending) = await RulesPassAsync(harness, rules, [person]);

        (coldPending, warmPending).Should().Be((1, 0));
        cold.Single().Should().BeEmpty();
        harness.PumpReads.Should().Be(1, "the figure the dataset gives is the older snapshot's, read once in the background");
        warm.Single().Should().ContainSingle().Which.Rule!.Value.Should().Be(12.3);
    }

    [Fact]
    public async Task A_newest_snapshot_older_than_the_day_is_no_figure_and_nothing_is_read()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(1), Pumps: [Pump(TimeSpan.FromHours(25), 12.34, 40)]), Devices);

        var (reasons, pending) = await RulesPassAsync(harness,
            [Rule("device_status", "reservoir", null, FlagRuleComparison.Below, 20)], [person]);
        await harness.Reads.WhenIdleAsync();

        pending.Should().Be(0);
        reasons.Single().Should().BeEmpty();
        harness.PumpReads.Should().Be(0);
    }

    [Fact]
    public async Task Only_people_whose_rule_judgements_are_returned_get_a_read_audit_row()
    {
        var harness = new Harness();
        var returned = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var readOnly = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        IReadOnlyList<FlagRule> rules = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];

        await RulesPassAsync(harness, rules, [returned, readOnly]);
        await harness.Reads.WhenIdleAsync();
        await RulesPassAsync(harness, rules, [returned]);

        harness.GlucoseReads.Should().Be(2, "both were read in the background");
        (await harness.AuditRowsAsync()).Should().ContainSingle().Which.TenantId.Should().Be(returned.Tenant.Id);
    }

    [Fact]
    public async Task A_rule_whose_fact_no_longer_checks_is_left_out_and_a_viewer_with_no_rule_that_checks_has_no_run()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var gone = Rule("glucose_statistics", "removedField", 14, FlagRuleComparison.Above, 4);

        var reasons = await RulesJudgedAsync(harness, [gone, Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)], [person]);
        var run = await Rules(harness, harness.Evaluator, [gone], [])
            .BeginAsync(Viewer, OverviewRuleSet.All, [], [person], false, CancellationToken.None);

        reasons.Single().Should().ContainSingle().Which.Rule!.Field.Should().Be("timeBelow");
        run.Should().BeNull();
    }

    private static readonly IReadOnlySet<string> Devices = new HashSet<string> { Scope.GlucoseRead, Scope.ReportsRead, Scope.DevicesRead };

    /// <summary>A pump snapshot <paramref name="age"/> before <see cref="Start"/>.</summary>
    private static PumpSnapshot Pump(TimeSpan age, double? reservoir, int? battery) =>
        new() { Timestamp = Start.UtcDateTime - age, Reservoir = reservoir, BatteryPercent = battery };

    private static FlagRule Rule(
        string dataset, string field, int? days, FlagRuleComparison comparison, double value,
        NotificationUrgency level = NotificationUrgency.Warn) =>
        new(Guid.NewGuid(), dataset, field, days, comparison, value, level, Start.UtcDateTime, Start.UtcDateTime);

    /// <summary>One overview pass of the viewer's rules over <paramref name="people"/>, each in their own scope.</summary>
    private static Task<(IReadOnlyList<IReadOnlyList<TenantOverviewAttention>> Reasons, int Pending)> RulesPassAsync(
        Harness harness,
        IReadOnlyList<FlagRule> rules,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours = false) =>
        RulesPassAsync(harness, harness.Evaluator, rules, people, credentialLimitTo24Hours);

    /// <summary>One overview pass of the rules over <paramref name="people"/> by the viewer of <paramref name="evaluator"/>.</summary>
    private static async Task<(IReadOnlyList<IReadOnlyList<TenantOverviewAttention>> Reasons, int Pending)> RulesPassAsync(
        Harness harness,
        FlagFactEvaluator evaluator,
        IReadOnlyList<FlagRule> rules,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours)
    {
        var (reasons, pending, _) = await ViewerPassAsync(
            harness, evaluator, OverviewRuleSet.All, rules, [], people, credentialLimitTo24Hours);
        return (reasons, pending);
    }

    /// <summary>
    /// One overview pass of <paramref name="set"/> over <paramref name="people"/> by the viewer of
    /// <paramref name="evaluator"/>, who holds <paramref name="personal"/> and is a member of <paramref name="teams"/>.
    /// </summary>
    private static async Task<(IReadOnlyList<IReadOnlyList<TenantOverviewAttention>> Reasons, int Pending, IReadOnlyList<TenantOverviewRuleTeam> Teams)> ViewerPassAsync(
        Harness harness,
        FlagFactEvaluator evaluator,
        OverviewRuleSet set,
        IReadOnlyList<FlagRule> personal,
        IReadOnlyList<(TeamAndItsPeople Team, IReadOnlyList<FlagRule> Rules)> teams,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours)
    {
        var run = await Rules(harness, evaluator, personal, teams)
            .BeginAsync(Viewer, set, [], people, credentialLimitTo24Hours, CancellationToken.None);
        run.Should().NotBeNull();
        var reasons = new List<IReadOnlyList<TenantOverviewAttention>>();
        foreach (var person in people)
        {
            var clamped = MemberScopeResolver.IsHistoryClamped(
                credentialLimitTo24Hours, person.MembershipLimitTo24Hours, person.EffectivePermissions);
            await using var scope = TenantOverviewService.OpenTenantScope(harness.Request, person.Tenant, clamped, "test");
            var latest = clamped
                ? null
                : await scope.ServiceProvider.GetRequiredService<ICanonicalGlucoseService>().GetLatestAsync(CancellationToken.None);
            var pump = await Harness.NewestPumpAsync(person, scope.ServiceProvider, CancellationToken.None);
            reasons.Add((await run!.JudgeAsync(person, scope.ServiceProvider, latest, [], pump, CancellationToken.None)).Reasons);
        }
        run!.Finish();
        return (reasons, run.PeopleWithRulesPending, run.Teams);
    }

    /// <summary>The rules of a viewer who holds <paramref name="personal"/> and is a member of <paramref name="teams"/>.</summary>
    private static ViewerFlagRules Rules(
        Harness harness,
        FlagFactEvaluator evaluator,
        IReadOnlyList<FlagRule> personal,
        IReadOnlyList<(TeamAndItsPeople Team, IReadOnlyList<FlagRule> Rules)> teams)
        => new(
            new RulesOf(personal, teams.ToDictionary(t => t.Team.Id, t => t.Rules)),
            TeamsOf(harness, [.. teams.Select(t => t.Team)]),
            evaluator,
            harness.Time);

    /// <summary>The teams of the viewer, as the store gives them at the harness's time.</summary>
    private static ITeamRepository TeamsOf(Harness harness, IReadOnlyList<TeamAndItsPeople> teams)
    {
        var repository = new Mock<ITeamRepository>(MockBehavior.Strict);
        repository.Setup(t => t.GetTeamsAndTheirPeopleAsync(Viewer, harness.Time.GetUtcNow().UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(teams);
        return repository.Object;
    }

    /// <summary>Each person's rule reasons once the background reads a first pass started are kept.</summary>
    private static async Task<IReadOnlyList<IReadOnlyList<TenantOverviewAttention>>> RulesJudgedAsync(
        Harness harness, IReadOnlyList<FlagRule> rules, IReadOnlyList<GlucoseReadTenant> people)
    {
        await RulesPassAsync(harness, rules, people);
        await harness.Reads.WhenIdleAsync();
        var (reasons, pending) = await RulesPassAsync(harness, rules, people);
        pending.Should().Be(0);
        return reasons;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(until, "the condition should hold within ten seconds");
            await Task.Delay(10);
        }
    }

    /// <summary>The viewer's own rules, and the rules of the teams they are a member of, as the store gives them.</summary>
    private sealed class RulesOf(
        IReadOnlyList<FlagRule> rules, IReadOnlyDictionary<Guid, IReadOnlyList<FlagRule>> ofTeams) : IFlagRuleRepository
    {
        public List<IReadOnlyCollection<Guid>> TeamReads { get; } = [];

        public int SubjectReads { get; private set; }

        public Task<IReadOnlyList<FlagRule>> GetOfSubjectAsync(Guid subjectId, CancellationToken ct)
        {
            SubjectReads++;
            return Task.FromResult(rules);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<FlagRule>>> GetOfTeamsAsync(
            Guid subjectId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct)
        {
            TeamReads.Add(teamIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<FlagRule>>>(
                ofTeams.Where(t => teamIds.Contains(t.Key)).ToDictionary());
        }

        public Task<IReadOnlyList<FlagRule>?> GetOfTeamAsync(Guid teamId, Guid callerSubjectId, DateTime now, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TeamFlagRuleWrite> CreateForTeamAsync(Guid teamId, Guid callerSubjectId, FlagRuleDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TeamFlagRuleWrite> UpdateForTeamAsync(
            Guid teamId, Guid callerSubjectId, Guid ruleId, FlagRuleDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TeamFlagRuleChange> DeleteForTeamAsync(Guid teamId, Guid callerSubjectId, Guid ruleId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<FlagRule?> CreateAsync(Guid subjectId, FlagRuleDraft draft, CancellationToken ct) => throw new NotSupportedException();

        public Task<FlagRule?> UpdateAsync(Guid subjectId, Guid ruleId, FlagRuleDraft draft, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid subjectId, Guid ruleId, CancellationToken ct) => throw new NotSupportedException();
    }
}
