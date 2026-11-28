using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The insulin and carbs on board facts, judged on the figures the overview read for the person: the harness registers
/// no <c>IIobCobReader</c>, so a judging that read them again would fail to resolve it.
/// </summary>
public partial class FlagFactEvaluatorTests
{
    private static readonly IReadOnlySet<string> OnBoardScopes =
        new HashSet<string> { Scope.GlucoseRead, Scope.ReportsRead, Scope.TreatmentsRead };

    private static FlagFactCondition InsulinOnBoard(Harness harness, FlagComparison comparison, double value) =>
        harness.Condition("current_status", "insulinOnBoard", null, comparison, value, "U");

    [Fact]
    public async Task A_rule_on_insulin_on_board_matches_on_the_figure_the_overview_read()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), LatestAge: TimeSpan.FromMinutes(5), OnBoard: new OnBoardFigures(1.23, 12)),
            OnBoardScopes);

        var judged = await harness.JudgeAsync(
            person, InsulinOnBoard(harness, FlagComparison.Above, 1), InsulinOnBoard(harness, FlagComparison.Above, 2));

        judged[0].Should().BeEquivalentTo(new { Outcome = FlagOutcome.Matched, Matches = true, Value = (double?)1.23 });
        judged[1].Should().BeEquivalentTo(new { Outcome = FlagOutcome.NotMatched, Matches = false, Value = (double?)1.23 });
    }

    [Fact]
    public async Task A_rule_on_carbs_on_board_is_judged_on_the_figure_the_overview_read()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), OnBoard: new OnBoardFigures(1.23, 12)), OnBoardScopes);

        var judged = await harness.JudgeAsync(
            person, harness.Condition("current_status", "carbsOnBoard", null, FlagComparison.Above, 10, "g"));

        judged.Single().Should().BeEquivalentTo(new { Outcome = FlagOutcome.Matched, Value = (double?)12 });
    }

    [Fact]
    public async Task Insulin_on_board_that_is_not_known_is_no_data_and_never_a_match()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), OnBoard: new OnBoardFigures(null, null)), OnBoardScopes);

        var judged = await harness.JudgeAsync(
            person, InsulinOnBoard(harness, FlagComparison.Below, 5), InsulinOnBoard(harness, FlagComparison.Above, 0));

        judged.Should().OnlyContain(j => j.Outcome == FlagOutcome.NoData && !j.Matches && j.Value == null);
    }

    [Fact]
    public async Task A_rule_that_needs_no_on_board_figure_reads_and_audits_none_whatever_the_caller_may_read()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), LatestAge: TimeSpan.FromMinutes(30), OnBoard: new OnBoardFigures(1.23, 12)),
            OnBoardScopes);

        await harness.JudgeAsync(person, HoursSinceReading(harness));

        (await harness.AuditRowsAsync()).Should().ContainSingle().Which.EntityType.Should().Be("glucose");
    }

    [Fact]
    public async Task A_rule_on_insulin_on_board_audits_a_read_of_treatments()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), OnBoard: new OnBoardFigures(1.23, 12)), OnBoardScopes);

        await harness.JudgeAsync(person, InsulinOnBoard(harness, FlagComparison.Above, 1));

        (await harness.AuditRowsAsync()).Should().ContainSingle().Which.EntityType.Should().Be("treatments");
    }

    [Fact]
    public async Task A_later_judging_in_the_period_that_reads_treatments_writes_its_own_audit_row()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), LatestAge: TimeSpan.FromMinutes(5), OnBoard: new OnBoardFigures(1.23, 12)),
            OnBoardScopes);

        await harness.JudgeAsync(person, HoursSinceReading(harness));
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.JudgeAsync(person, InsulinOnBoard(harness, FlagComparison.Above, 1));

        (await harness.AuditRowsAsync()).Select(r => r.EntityType).Should().Equal("glucose", "glucose,treatments");
    }

    [Fact]
    public async Task Judgings_in_the_period_that_read_the_same_categories_write_one_audit_row_per_set_of_categories()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), LatestAge: TimeSpan.FromMinutes(5), OnBoard: new OnBoardFigures(1.23, 12)),
            OnBoardScopes);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await harness.JudgeAsync(person, HoursSinceReading(harness));
            await harness.JudgeAsync(person, InsulinOnBoard(harness, FlagComparison.Above, 1));
            await harness.JudgeAsync(person, harness.Condition("current_status", "carbsOnBoard", null, FlagComparison.Above, 10, "g"));
            harness.Time.Advance(TimeSpan.FromMinutes(1));
        }

        (await harness.AuditRowsAsync()).Select(r => r.EntityType).Should().Equal("glucose", "glucose,treatments");
    }

    [Fact]
    public async Task A_caller_without_treatments_read_cannot_check_insulin_on_board()
    {
        var harness = new Harness();
        var person = harness.Follow(
            new Person(Pattern, TimeSpan.FromDays(1), OnBoard: new OnBoardFigures(9, 9)), Reports);

        var judgement = (await harness.JudgeAsync(person, InsulinOnBoard(harness, FlagComparison.Above, 1))).Single();

        judgement.Outcome.Should().Be(FlagOutcome.CannotBeChecked);
        judgement.MissingScope.Should().Be(Scope.TreatmentsRead);
        judgement.Matches.Should().BeFalse();
    }
}
