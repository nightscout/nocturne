using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Reports;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The fact filters of a People query judged with the rules of the read (<see cref="ViewerFlagRules"/>): a person matches
/// only when every filter's fact was judged and met, never while it is still being checked, cannot be checked, or names
/// a fact that no longer checks.
/// </summary>
public partial class FlagFactEvaluatorTests
{
    private static PeopleFilter FactFilter(string field, FlagRuleComparison comparison, double value, int? days = 14) =>
        new(PeopleFilterKind.Fact, "glucose_statistics", field, days, comparison, value, null, null, null, null);

    [Fact]
    public async Task A_fact_filter_matches_once_judged_a_person_whose_fact_meets_it_and_no_one_whose_fact_does_not()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var meets = FactFilter("timeBelow", FlagRuleComparison.Above, 4);
        var misses = FactFilter("timeBelow", FlagRuleComparison.Above, 50);

        var (cold, coldPending) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets], [person]);
        await harness.Reads.WhenIdleAsync();
        var (warm, warmPending) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets], [person]);
        var (missed, _) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [misses], [person]);

        cold.Single().MatchesFilters.Should().BeFalse("a person still being checked matches no filter");
        coldPending.Should().Be(1, "the person still being checked is counted");
        warm.Single().MatchesFilters.Should().BeTrue();
        warm.Single().Reasons.Should().BeEmpty("a filter gives no reason");
        warmPending.Should().Be(0);
        missed.Single().MatchesFilters.Should().BeFalse();
    }

    [Fact]
    public async Task A_person_a_fact_filter_cannot_be_checked_for_matches_nothing_and_is_not_pending()
    {
        var harness = new Harness();
        var data = new Person(Pattern, TimeSpan.FromDays(15));
        var person = harness.Follow(data);
        var limited = harness.Follow(data, limitTo24Hours: true);
        var meets = FactFilter("timeBelow", FlagRuleComparison.Above, 4);
        await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets], [person]);
        await harness.Reads.WhenIdleAsync();

        var (byCredential, credentialPending) =
            await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets], [person], credentialLimitTo24Hours: true);
        var (byMembership, membershipPending) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets], [limited]);

        byCredential.Single().MatchesFilters.Should().BeFalse();
        byMembership.Single().MatchesFilters.Should().BeFalse();
        (credentialPending, membershipPending).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_person_without_the_scope_a_fact_filter_needs_matches_nothing_and_is_not_pending()
    {
        var harness = new Harness();
        var data = new Person(Pattern, TimeSpan.FromDays(15));
        var noTreatments = harness.Follow(data, Reports);
        var noReports = harness.Follow(data, new HashSet<string> { Scope.GlucoseRead });
        var insulin = FactFilter("tdd", FlagRuleComparison.Above, 40);
        var timeBelow = FactFilter("timeBelow", FlagRuleComparison.Above, 4);

        await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [insulin], [noTreatments]);
        await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [timeBelow], [noReports]);
        await harness.Reads.WhenIdleAsync();
        var (byDataset, datasetPending) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [insulin], [noTreatments]);
        var (byPeriod, periodPending) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [timeBelow], [noReports]);

        byDataset.Single().MatchesFilters.Should().BeFalse("insulin totals need treatments.read");
        byPeriod.Single().MatchesFilters.Should().BeFalse("a period fact needs reports.read");
        (datasetPending, periodPending).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_fact_filter_read_writes_the_read_audit_row_a_rule_read_writes_only_for_the_people_it_returns()
    {
        var harness = new Harness();
        var returned = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var readOnly = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var meets = FactFilter("timeBelow", FlagRuleComparison.Above, 4);

        await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets], [returned, readOnly]);
        await harness.Reads.WhenIdleAsync();
        await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets], [returned]);

        harness.GlucoseReads.Should().Be(2, "both were read in the background");
        var row = (await harness.AuditRowsAsync()).Should().ContainSingle().Subject;
        (row.TenantId, row.ActorSubjectId, row.Endpoint).Should().Be((returned.Tenant.Id, Viewer, FlagFactEvaluator.AuditEndpoint));
    }

    [Fact]
    public async Task Every_fact_filter_must_match()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var meets = FactFilter("timeBelow", FlagRuleComparison.Above, 4);
        var alsoMeets = FactFilter("timeBelow", FlagRuleComparison.Below, 50);
        var misses = FactFilter("timeBelow", FlagRuleComparison.Above, 50);

        await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets, alsoMeets, misses], [person]);
        await harness.Reads.WhenIdleAsync();
        var (both, _) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets, alsoMeets], [person]);
        var (one, _) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [meets, misses], [person]);

        both.Single().MatchesFilters.Should().BeTrue();
        one.Single().MatchesFilters.Should().BeFalse();
    }

    [Fact]
    public async Task A_fact_filter_whose_fact_no_longer_checks_matches_no_one()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var gone = FactFilter("removedField", FlagRuleComparison.Above, 4);
        var meets = FactFilter("timeBelow", FlagRuleComparison.Above, 4);

        await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [gone, meets], [person]);
        await harness.Reads.WhenIdleAsync();
        var (judged, pending) = await FiltersPassAsync(harness, OverviewRuleSet.Fixed, [], [], [gone, meets], [person]);

        judged.Single().MatchesFilters.Should().BeFalse();
        pending.Should().Be(0);
    }

    [Fact]
    public async Task A_team_read_with_a_fact_filter_as_the_wall_makes_gives_the_teams_reasons_and_never_reads_the_viewers_own_rules()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        IReadOnlyList<FlagRule> personal = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 1)];
        IReadOnlyList<FlagRule> ofTeam = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];
        var ward = Team("Ward 4", person);
        var rules = new RulesOf(personal, new Dictionary<Guid, IReadOnlyList<FlagRule>> { [ward.Id] = ofTeam });
        var viewerRules = new ViewerFlagRules(rules, TeamsOf(harness, [ward]), harness.Evaluator, harness.Time);
        var meets = FactFilter("timeBelow", FlagRuleComparison.Above, 4);

        await FiltersPassAsync(harness, viewerRules, OverviewRuleSet.Team, [meets], [person], false);
        await harness.Reads.WhenIdleAsync();
        var (judged, _) = await FiltersPassAsync(harness, viewerRules, OverviewRuleSet.Team, [meets], [person], false);

        judged.Single().MatchesFilters.Should().BeTrue();
        judged.Single().Reasons.Should().ContainSingle().Which.Rule!.Source.Should().Be(FlagRuleSource.Team);
        rules.SubjectReads.Should().Be(0, "a team read never loads the viewer's own rules");
    }

    [Fact]
    public async Task A_fact_filter_is_judged_by_its_own_condition_and_a_rule_by_its_own_when_both_are_held()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var meets = FactFilter("timeBelow", FlagRuleComparison.Above, 4);
        var misses = FactFilter("timeBelow", FlagRuleComparison.Above, 50);
        IReadOnlyList<FlagRule> missingRule = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 50)];
        IReadOnlyList<FlagRule> meetingRule = [Rule("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4)];
        await FiltersPassAsync(harness, OverviewRuleSet.All, missingRule, [], [meets], [person]);
        await harness.Reads.WhenIdleAsync();

        var (filterOnly, _) = await FiltersPassAsync(harness, OverviewRuleSet.All, missingRule, [], [meets], [person]);
        var (ruleOnly, _) = await FiltersPassAsync(harness, OverviewRuleSet.All, meetingRule, [], [misses], [person]);

        filterOnly.Single().MatchesFilters.Should().BeTrue("the filter's own condition is met");
        filterOnly.Single().Reasons.Should().BeEmpty("the rule's condition is not met");
        ruleOnly.Single().MatchesFilters.Should().BeFalse("the filter's own condition is not met");
        ruleOnly.Single().Reasons.Should().ContainSingle("the rule's condition is met");
    }

    /// <summary>One overview pass of <paramref name="set"/> and <paramref name="filters"/> over <paramref name="people"/>.</summary>
    private static Task<(IReadOnlyList<ViewerJudgement> Judged, int Pending)> FiltersPassAsync(
        Harness harness,
        OverviewRuleSet set,
        IReadOnlyList<FlagRule> personal,
        IReadOnlyList<(TeamAndItsPeople Team, IReadOnlyList<FlagRule> Rules)> teams,
        IReadOnlyList<PeopleFilter> filters,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours = false) =>
        FiltersPassAsync(
            harness, Rules(harness, harness.Evaluator, personal, teams), set, filters, people, credentialLimitTo24Hours);

    private static async Task<(IReadOnlyList<ViewerJudgement> Judged, int Pending)> FiltersPassAsync(
        Harness harness,
        ViewerFlagRules rules,
        OverviewRuleSet set,
        IReadOnlyList<PeopleFilter> filters,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours)
    {
        var run = await rules.BeginAsync(Viewer, set, filters, people, credentialLimitTo24Hours, CancellationToken.None);
        run.Should().NotBeNull("a read with a fact filter judges it");
        var judged = new List<ViewerJudgement>();
        foreach (var person in people)
        {
            var clamped = MemberScopeResolver.IsHistoryClamped(
                credentialLimitTo24Hours, person.MembershipLimitTo24Hours, person.EffectivePermissions);
            await using var scope = TenantOverviewService.OpenTenantScope(harness.Request, person.Tenant, clamped, "test");
            var latest = clamped
                ? null
                : await scope.ServiceProvider.GetRequiredService<ICanonicalGlucoseService>().GetLatestAsync(CancellationToken.None);
            judged.Add(await run!.JudgeAsync(person, scope.ServiceProvider, latest, [], null, null, CancellationToken.None));
        }
        run!.Finish();
        return (judged, run.PeopleWithRulesPending);
    }
}
