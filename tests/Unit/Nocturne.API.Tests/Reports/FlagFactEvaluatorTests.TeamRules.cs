using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// A team's flag rules judged with the viewer's own (<see cref="ViewerFlagRules"/>): which people a team's rule is
/// judged for, what a <see cref="OverviewRuleSet.Team"/> read leaves out, a personal rule shown once beside a team's
/// equal rule, two teams' equal rules, the teams a read names, and the one judging both kinds share.
/// </summary>
public partial class FlagFactEvaluatorTests
{
    private static readonly IReadOnlyList<FlagRule> NoRules = [];

    [Fact]
    public async Task Two_staff_of_a_team_and_the_wall_get_the_same_team_reasons_for_a_person_the_team_follows()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var rule = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, NotificationUrgency.Hazard);
        var ward = Team("Ward 4", person);
        var other = harness.For(Guid.NewGuid());

        await ViewerPassAsync(harness, harness.Evaluator, OverviewRuleSet.All, NoRules, [(ward, [rule])], [person], false);
        await harness.Reads.WhenIdleAsync();
        var (first, _, _) = await ViewerPassAsync(harness, harness.Evaluator, OverviewRuleSet.All, NoRules, [(ward, [rule])], [person], false);
        var (second, _, _) = await ViewerPassAsync(harness, other, OverviewRuleSet.All, NoRules, [(ward, [rule])], [person], false);
        var (wall, _, _) = await ViewerPassAsync(harness, harness.Evaluator, OverviewRuleSet.Team, NoRules, [(ward, [rule])], [person], false);

        var reason = first.Single().Should().ContainSingle().Subject.Rule!;
        (reason.RuleId, reason.Source, reason.Team).Should().Be((rule.Id, FlagRuleSource.Team, new TenantOverviewRuleTeam(ward.Id, "Ward 4")));
        second.Single().Should().BeEquivalentTo(first.Single());
        wall.Single().Should().BeEquivalentTo(first.Single());
        harness.GlucoseReads.Should().Be(1, "every viewer of the person shares one keep");
    }

    [Fact]
    public async Task A_teams_rule_is_neither_judged_nor_read_for_a_person_the_team_does_not_follow()
    {
        var harness = new Harness();
        var followed = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var direct = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var ward = Team("Ward 4", followed);
        IReadOnlyList<FlagRule> rules = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];

        var (cold, coldPending, _) = await ViewerPassAsync(
            harness, harness.Evaluator, OverviewRuleSet.Team, NoRules, [(ward, rules)], [followed, direct], false);
        await harness.Reads.WhenIdleAsync();
        var (warm, _, _) = await ViewerPassAsync(
            harness, harness.Evaluator, OverviewRuleSet.Team, NoRules, [(ward, rules)], [followed, direct], false);

        coldPending.Should().Be(1, "only the person the team follows waits on a read");
        cold[1].Should().BeEmpty();
        warm[0].Should().ContainSingle();
        warm[1].Should().BeEmpty();
        harness.GlucoseReads.Should().Be(1, "nothing of a person the team does not follow is read for its rule");
    }

    [Fact]
    public async Task A_team_read_judges_the_teams_rules_without_reading_or_giving_the_viewers_own()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        IReadOnlyList<FlagRule> personal = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 1)];
        IReadOnlyList<FlagRule> ofTeam = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];
        var ward = Team("Ward 4", person);
        var rules = new RulesOf(personal, new Dictionary<Guid, IReadOnlyList<FlagRule>> { [ward.Id] = ofTeam });

        var reasons = await JudgedTwiceAsync(harness, OverviewRuleSet.Team, personal, [(ward, ofTeam)], [person]);
        var onlyPersonal = await Rules(harness, harness.Evaluator, personal, [])
            .BeginAsync(Viewer, OverviewRuleSet.Team, [], [person], false, CancellationToken.None);
        await new ViewerFlagRules(rules, TeamsOf(harness, [ward]), harness.Evaluator, harness.Time)
            .BeginAsync(Viewer, OverviewRuleSet.Team, [], [person], false, CancellationToken.None);

        reasons.Single().Should().ContainSingle().Which.Rule!.Source.Should().Be(FlagRuleSource.Team);
        onlyPersonal.Should().BeNull("a team read with no team rule judges nothing");
        rules.SubjectReads.Should().Be(0, "a team read never loads the viewer's own rules");
    }

    [Fact]
    public async Task An_all_read_gives_the_teams_reasons_and_the_viewers_own()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        IReadOnlyList<FlagRule> personal = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 1)];
        IReadOnlyList<FlagRule> ofTeam = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];

        var reasons = await JudgedTwiceAsync(harness, OverviewRuleSet.All, personal, [(Team("Ward 4", person), ofTeam)], [person]);

        reasons.Single().Select(r => r.Rule!.Source).Should().BeEquivalentTo([FlagRuleSource.Team, FlagRuleSource.Yours]);
        harness.GlucoseReads.Should().Be(1, "a team's rule and the viewer's on one read share it");
    }

    [Fact]
    public async Task A_personal_rule_equal_to_a_teams_shows_once_as_the_teams_and_one_that_differs_in_value_shows_beside_it()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var ofTeam = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, NotificationUrgency.Info);
        var equal = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, NotificationUrgency.Hazard);
        var higher = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 5);
        var longer = Rule("glucose_statistics", "timeBelow", 30, FlagRuleComparison.Above, 4);
        var ward = Team("Ward 4", person);

        var reasons = await JudgedTwiceAsync(harness, OverviewRuleSet.All, [equal, higher, longer], [(ward, [ofTeam])], [person]);

        reasons.Single().Select(r => r.Rule!.RuleId).Should().BeEquivalentTo([ofTeam.Id, higher.Id, longer.Id]);
        reasons.Single().Single(r => r.Rule!.RuleId == ofTeam.Id).Level.Should().Be(NotificationUrgency.Info, "the team's reason is the one shown");
    }

    [Fact]
    public async Task Two_teams_that_follow_a_person_with_equal_rules_each_keep_their_reason()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var ward = Team("Ward 4", person);
        var clinic = Team("Clinic", person);

        var reasons = await JudgedTwiceAsync(harness, OverviewRuleSet.Team, NoRules,
            [(ward, [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)]),
                (clinic, [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)])],
            [person]);

        reasons.Single().Select(r => r.Rule!.Team!.Name).Should().BeEquivalentTo(["Ward 4", "Clinic"]);
    }

    [Fact]
    public async Task A_read_names_the_teams_whose_rules_apply_to_one_of_its_people_and_no_team_the_caller_manages()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var elsewhere = Guid.NewGuid();
        var rule = Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4);
        var managed = Team("Managed", person) with { CallerIsStaff = false };

        var (_, _, teams) = await ViewerPassAsync(harness, harness.Evaluator, OverviewRuleSet.Team, NoRules,
            [
                (Team("Ward 5", person), [rule]),
                (Team("clinic", person), [rule]),
                (Team("Ward 4", elsewhere), [rule]),
                (Team("No rules", person), []),
                (managed, [rule]),
            ],
            [person], false);

        teams.Select(t => t.Name).Should().Equal("clinic", "Ward 5");
    }

    [Fact]
    public async Task A_team_the_caller_only_manages_gives_them_no_reason()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var managed = Team("Managed", person) with { CallerIsStaff = false };

        var run = await Rules(harness, harness.Evaluator, NoRules,
                [(managed, [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)])])
            .BeginAsync(Viewer, OverviewRuleSet.Team, [], [person], false, CancellationToken.None);

        run.Should().BeNull();
    }

    [Fact]
    public void A_personal_reason_is_dropped_only_where_a_team_reason_has_its_fact_days_comparison_and_value()
    {
        var team = new TenantOverviewRuleTeam(Guid.NewGuid(), "Ward 4");
        var ofTeam = Reason(team, "timeBelow", 14, FlagRuleComparison.Above, 4);
        var same = Reason(null, "timeBelow", 14, FlagRuleComparison.Above, 4);
        IReadOnlyList<TenantOverviewAttention> differing =
        [
            Reason(null, "timeAbove", 14, FlagRuleComparison.Above, 4),
            Reason(null, "timeBelow", 30, FlagRuleComparison.Above, 4),
            Reason(null, "timeBelow", 14, FlagRuleComparison.Below, 4),
            Reason(null, "timeBelow", 14, FlagRuleComparison.Above, 4.5),
        ];

        ViewerFlagRules.ShownOnce([ofTeam, same, .. differing]).Should().Equal([ofTeam, .. differing]);
        ViewerFlagRules.ShownOnce([same]).Should().Equal(same);
    }

    private static TenantOverviewAttention Reason(
        TenantOverviewRuleTeam? team, string field, int days, FlagRuleComparison comparison, double value) =>
        new(TenantOverviewAttentionKind.FlagRule, NotificationUrgency.Warn)
        {
            Rule = new TenantOverviewRuleReason(
                Guid.NewGuid(), team is null ? FlagRuleSource.Yours : FlagRuleSource.Team, team, "glucose_statistics", field,
                field, "%", "consensus", 10, comparison, value, days, null, null, null, null),
        };

    /// <summary>A team the viewer is staff of that follows <paramref name="people"/>.</summary>
    private static TeamAndItsPeople Team(string name, params GlucoseReadTenant[] people) =>
        new(Guid.NewGuid(), name, true, people.Select(p => p.Tenant.Id).ToHashSet());

    private static TeamAndItsPeople Team(string name, Guid site) => new(Guid.NewGuid(), name, true, new HashSet<Guid> { site });

    /// <summary>Each person's reasons for <paramref name="set"/> once the background reads a first pass started are kept.</summary>
    private static async Task<IReadOnlyList<IReadOnlyList<TenantOverviewAttention>>> JudgedTwiceAsync(
        Harness harness,
        OverviewRuleSet set,
        IReadOnlyList<FlagRule> personal,
        IReadOnlyList<(TeamAndItsPeople Team, IReadOnlyList<FlagRule> Rules)> teams,
        IReadOnlyList<GlucoseReadTenant> people)
    {
        await ViewerPassAsync(harness, harness.Evaluator, set, personal, teams, people, false);
        await harness.Reads.WhenIdleAsync();
        var (reasons, pending, _) = await ViewerPassAsync(harness, harness.Evaluator, set, personal, teams, people, false);
        pending.Should().Be(0);
        return reasons;
    }
}
