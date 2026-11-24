using Nocturne.Connectors.GoogleHealth.Services;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthStateTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_range_with_no_cursor_is_not_finished() =>
        Assert.False(GoogleHealthState.BackfillComplete(null, Day));

    [Fact]
    public void A_cursor_at_its_floor_is_finished() =>
        Assert.True(GoogleHealthState.BackfillComplete(Day, Day));

    [Fact]
    public void A_cursor_below_its_floor_is_finished() =>
        Assert.True(GoogleHealthState.BackfillComplete(Day.AddDays(-1), Day));

    [Fact]
    public void A_cursor_above_its_floor_is_not_finished() =>
        Assert.False(GoogleHealthState.BackfillComplete(Day.AddDays(1), Day));

    [Fact]
    public void A_floor_moved_below_a_finished_cursor_makes_it_unfinished()
    {
        Assert.True(GoogleHealthState.BackfillComplete(Day, Day));
        Assert.False(GoogleHealthState.BackfillComplete(Day, Day.AddMonths(-1)));
    }

    [Fact]
    public void A_range_with_no_floor_aims_for_the_earliest_supported_date()
    {
        Assert.False(GoogleHealthState.BackfillComplete(Day, null));
        Assert.True(GoogleHealthState.BackfillComplete(GoogleHealthState.EarliestSupportedDate, null));
    }
}
