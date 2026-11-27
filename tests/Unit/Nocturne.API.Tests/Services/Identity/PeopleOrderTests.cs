using FluentAssertions;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>The order of a read's people under a <see cref="PeopleSort"/> (<see cref="PeopleOrder"/>).</summary>
public class PeopleOrderTests
{
    private static readonly DateTime Now = new(2026, 11, 28, 9, 0, 0, DateTimeKind.Utc);

    public static TheoryData<PeopleSortColumn, PeopleSortDirection> NowSorts()
    {
        var data = new TheoryData<PeopleSortColumn, PeopleSortDirection>();
        foreach (var column in PeopleSort.NowColumns)
            foreach (var direction in Enum.GetValues<PeopleSortDirection>())
                data.Add(column, direction);
        return data;
    }

    public static TheoryData<PeopleSortColumn, PeopleSortDirection> ReviewSorts()
    {
        var data = new TheoryData<PeopleSortColumn, PeopleSortDirection>();
        foreach (var column in PeopleSort.ReviewColumns)
            foreach (var direction in Enum.GetValues<PeopleSortDirection>())
                data.Add(column, direction);
        return data;
    }

    [Theory]
    [MemberData(nameof(NowSorts))]
    public void A_now_column_orders_by_its_value_either_way_with_an_empty_value_last(
        PeopleSortColumn column, PeopleSortDirection direction)
    {
        var low = Item("b-low", column, 10);
        var high = Item("a-high", column, 20);
        var empty = Item("0-empty", column, null);

        var ordered = Ordered([empty, high, low], new PeopleSort(column, direction));

        ordered.Should().Equal(direction == PeopleSortDirection.Ascending ? [low, high, empty] : [high, low, empty]);
    }

    [Theory]
    [MemberData(nameof(ReviewSorts))]
    public void A_review_column_orders_the_review_by_its_value_either_way_with_an_empty_value_last(
        PeopleSortColumn column, PeopleSortDirection direction)
    {
        var low = Person("b-low", column, 10);
        var high = Person("a-high", column, 20);
        var empty = Person("0-empty", column, null);
        var sort = new PeopleSort(column, direction);

        List<TenantReviewPerson> people = [empty, high, low];
        people.Sort((a, b) => PeopleOrder.CompareReview(a, b, sort, new HashSet<Guid>()));

        people.Should().Equal(direction == PeopleSortDirection.Ascending ? [low, high, empty] : [high, low, empty]);
    }

    [Theory]
    [InlineData(PeopleSortDirection.Ascending)]
    [InlineData(PeopleSortDirection.Descending)]
    public void The_name_column_orders_by_display_name_ignoring_case(PeopleSortDirection direction)
    {
        var alice = Item("alice", PeopleSortColumn.Glucose, null);
        var bob = Item("Bob", PeopleSortColumn.Glucose, null);
        var carol = Item("carol", PeopleSortColumn.Glucose, null);

        var ordered = Ordered([bob, carol, alice], new PeopleSort(PeopleSortColumn.Name, direction));

        ordered.Should().Equal(direction == PeopleSortDirection.Ascending ? [alice, bob, carol] : [carol, bob, alice]);
    }

    [Fact]
    public void Flagged_people_stay_first_and_the_sort_orders_within_them_and_within_the_rest()
    {
        var sort = new PeopleSort(PeopleSortColumn.Glucose, PeopleSortDirection.Descending);
        var flaggedLow = Item("flagged-low", PeopleSortColumn.Glucose, 60, flagged: true);
        var flaggedHigh = Item("flagged-high", PeopleSortColumn.Glucose, 300, flagged: true);
        var quietLow = Item("quiet-low", PeopleSortColumn.Glucose, 100);
        var quietHigh = Item("quiet-high", PeopleSortColumn.Glucose, 170);

        Ordered([quietLow, flaggedLow, quietHigh, flaggedHigh], sort)
            .Should().Equal(flaggedHigh, flaggedLow, quietHigh, quietLow);
    }

    [Fact]
    public void Flagged_people_stay_first_in_the_review_under_a_review_sort()
    {
        var sort = new PeopleSort(PeopleSortColumn.TimeInRange, PeopleSortDirection.Descending);
        var flagged = Person("flagged", PeopleSortColumn.TimeInRange, 40);
        var quiet = Person("quiet", PeopleSortColumn.TimeInRange, 90);

        List<TenantReviewPerson> people = [quiet, flagged];
        people.Sort((a, b) => PeopleOrder.CompareReview(a, b, sort, new HashSet<Guid> { flagged.TenantId }));

        people.Should().Equal(flagged, quiet);
    }

    [Fact]
    public void A_tie_goes_by_display_name_then_slug()
    {
        var sort = new PeopleSort(PeopleSortColumn.Alerts, PeopleSortDirection.Descending);
        var second = Item("same", PeopleSortColumn.Alerts, 2) with { Slug = "same-2" };
        var first = Item("same", PeopleSortColumn.Alerts, 2) with { Slug = "same-1" };
        var named = Item("Abel", PeopleSortColumn.Alerts, 2);

        Ordered([second, first, named], sort).Should().Equal(named, first, second);
    }

    [Fact]
    public void No_sort_and_a_review_column_leave_the_overview_in_its_own_order()
    {
        var flagged = Item("zed", PeopleSortColumn.Glucose, 300, flagged: true);
        var quiet = Item("abe", PeopleSortColumn.Glucose, 100);
        List<TenantOverviewItem> expected = [quiet, flagged];
        expected.Sort(TenantOverviewService.CompareForDisplay);

        Ordered([quiet, flagged], null).Should().Equal(expected);
        Ordered([quiet, flagged], new PeopleSort(PeopleSortColumn.Gmi, PeopleSortDirection.Ascending)).Should().Equal(expected);
    }

    private static List<TenantOverviewItem> Ordered(List<TenantOverviewItem> items, PeopleSort? sort)
    {
        var ordered = items.ToList();
        ordered.Sort((a, b) => PeopleOrder.Compare(a, b, sort, Now));
        return ordered;
    }

    /// <summary>An item whose <paramref name="column"/> holds <paramref name="value"/>, or nothing when it is null.</summary>
    private static TenantOverviewItem Item(string name, PeopleSortColumn column, double? value, bool flagged = false) =>
        new(
            Guid.NewGuid(), name, name,
            column == PeopleSortColumn.ReadingAge && value is { } age ? Now.AddSeconds(-age) : null,
            column == PeopleSortColumn.Glucose && value is { } mgdl ? new TenantOverviewReading(mgdl, null, null, null, Now) : null,
            GlucoseStatus.InRange,
            new TenantOverviewThresholds(55, 70, 180, 250),
            column == PeopleSortColumn.Alerts && value is { } alerts ? (int)alerts : null,
            null,
            [],
            null,
            column switch
            {
                PeopleSortColumn.Reservoir when value is { } units => new TenantOverviewDevices(units, null, Now),
                PeopleSortColumn.PumpBattery when value is { } percent => new TenantOverviewDevices(null, (int)percent, Now),
                _ => null,
            },
            null,
            flagged ? [new TenantOverviewAttention(TenantOverviewAttentionKind.GlucoseOutOfRange, NotificationUrgency.Hazard)] : [],
            null);

    /// <summary>A review person whose <paramref name="column"/> holds <paramref name="value"/>, or nothing when it is null.</summary>
    private static TenantReviewPerson Person(string name, PeopleSortColumn column, double? value) =>
        new(
            Guid.NewGuid(), name, name, null,
            new TenantReviewStatistics(
                column == PeopleSortColumn.TimeInRange && value is { } target ? new TimeInRangePercentages { Target = target } : null,
                column == PeopleSortColumn.Gmi ? value : null,
                column == PeopleSortColumn.Cv ? value : null,
                column == PeopleSortColumn.Wear ? value : null,
                null,
                column == PeopleSortColumn.TimeInRangeChange ? value : null));
}
