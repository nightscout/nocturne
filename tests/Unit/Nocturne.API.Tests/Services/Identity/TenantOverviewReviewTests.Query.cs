using FluentAssertions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;
using static Nocturne.API.Tests.Services.Identity.TenantOverviewTestKit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>The review read of a <see cref="PeopleQuery"/>: the people the overview read of it keeps, in its order or a review column's.</summary>
public partial class TenantOverviewReviewTests
{
    private static PeopleFilter QueryFilter(PeopleFilterKind kind) =>
        new(kind, null, null, null, null, null, null, null, null, null);

    [Fact]
    public async Task A_review_keeps_the_people_a_fact_filter_matched_and_counts_the_people_still_pending()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var matched = SeedMembership(options, subjectId, "matched", ReportReader);
        var unmatched = SeedMembership(options, subjectId, "unmatched", ReportReader);
        var rules = new ScriptedRules { Pending = 1, FailingFilters = { unmatched } };
        var harness = new Harness(options, rules);
        var fact = QueryFilter(PeopleFilterKind.Fact) with
        {
            Dataset = "glucose_statistics", Field = "timeBelow", Days = 14, Comparison = FlagRuleComparison.Above, Value = 4,
        };

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, 14, OverviewRuleSet.All, new PeopleQuery(null, [fact], null));

        review.People.Select(p => p.TenantId).Should().Equal(matched);
        review.PeopleWithRulesPending.Should().Be(1);
        harness.Reads.Select(r => r.Tenant).Should().NotContain(unmatched, "no figure of a person the query leaves out is read");
    }

    [Theory]
    [InlineData(PeopleSortDirection.Ascending, new[] { "ruled", "low", "high" })]
    [InlineData(PeopleSortDirection.Descending, new[] { "ruled", "high", "low" })]
    public async Task A_review_column_sort_orders_the_review_with_the_flagged_first(PeopleSortDirection direction, string[] order)
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var low = SeedMembership(options, subjectId, "low", ReportReader);
        var high = SeedMembership(options, subjectId, "high", ReportReader);
        var ruled = SeedMembership(options, subjectId, "ruled", ReportReader);
        var rules = new ScriptedRules { Reasons = { [ruled] = [RuleReason(NotificationUrgency.Warn, null)] } };
        var harness = new Harness(options, rules);
        harness.Series[low] = Harness.Period(current: 1, previous: 1);
        harness.Series[high] = Harness.Period(current: 3, previous: 3);
        harness.Series[ruled] = Harness.Period(current: 2, previous: 2);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, 14, OverviewRuleSet.All,
            new PeopleQuery(null, null, new PeopleSort(PeopleSortColumn.TimeInRange, direction)));

        review.People.Select(p => p.Slug).Should().Equal(order);
    }

    [Fact]
    public async Task A_review_with_a_now_sort_or_the_flagged_filter_is_in_the_overviews_order()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var first = SeedMembership(options, subjectId, "b-first", ReportReader);
        var second = SeedMembership(options, subjectId, "a-second", ReportReader);
        var quiet = SeedMembership(options, subjectId, "quiet", ReportReader);
        var rules = new ScriptedRules
        {
            Reasons =
            {
                [first] = [RuleReason(NotificationUrgency.Hazard, null)],
                [second] = [RuleReason(NotificationUrgency.Info, null)],
            },
        };
        var harness = new Harness(options, rules);

        var flagged = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, 14, OverviewRuleSet.All,
            new PeopleQuery(null, [QueryFilter(PeopleFilterKind.Flagged)], null));
        var byName = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, 14, OverviewRuleSet.All,
            new PeopleQuery(null, null, new PeopleSort(PeopleSortColumn.Name, PeopleSortDirection.Descending)));

        flagged.People.Select(p => p.TenantId).Should().Equal(first, second);
        byName.People.Select(p => p.TenantId).Should().Equal(first, second, quiet);
    }

    [Fact]
    public async Task A_review_search_keeps_the_people_whose_name_holds_it_and_reads_no_one_else()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var alice = SeedMembership(options, subjectId, "alice", ReportReader);
        var bob = SeedMembership(options, subjectId, "bob", ReportReader);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, 14, OverviewRuleSet.Fixed, new PeopleQuery("ALI", null, null));

        review.People.Select(p => p.TenantId).Should().Equal(alice);
        harness.Reads.Select(r => r.Tenant).Should().NotContain(bob);
    }
}
