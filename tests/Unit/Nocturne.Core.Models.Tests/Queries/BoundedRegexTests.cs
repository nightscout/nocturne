using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.Core.Models.Tests.Queries;

public class BoundedRegexTests
{
    [Fact]
    public void ASpentBudget_StopsEveryLaterMatch()
    {
        var regex = BoundedRegex.Create("^a", RegexOptions.None, budget: TimeSpan.Zero);

        regex.IsMatch("abc").Should().BeTrue();
        regex.IsMatch("abc").Should().BeFalse();
        regex.Abandoned.Should().BeTrue();
    }

    [Fact]
    public void TheBudgetIsSharedAcrossRows()
    {
        var regex = BoundedRegex.Create(".*a[ab]{200}c", RegexOptions.None, budget: TimeSpan.FromMilliseconds(50));
        var random = new Random(1);
        var row = new string(Enumerable.Range(0, 2_000).Select(_ => random.Next(2) == 0 ? 'a' : 'b').ToArray());

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 2_000; i++)
            regex.IsMatch(row);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        regex.Abandoned.Should().BeTrue();
        regex.IsMatch(row + "c").Should().BeFalse();
    }

    [Fact]
    public void AnOrdinaryPattern_MatchesAcrossManyRows()
    {
        var regex = BoundedRegex.Create("^2015-04.*T1[34]");

        for (var i = 0; i < 10_000; i++)
            regex.IsMatch("2015-04-02T13:05:00.000Z").Should().BeTrue();
        regex.IsMatch("2015-05-02T13:05:00.000Z").Should().BeFalse();
        regex.Abandoned.Should().BeFalse();
    }
}
