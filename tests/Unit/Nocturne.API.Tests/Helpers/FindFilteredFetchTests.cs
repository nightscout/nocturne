using FluentAssertions;
using Nocturne.API.Helpers;
using Xunit;

namespace Nocturne.API.Tests.Helpers;

[Trait("Category", "Unit")]
public class FindFilteredFetchTests
{
    private static readonly string[] Source = Enumerable.Range(0, 1_000).Select(i => $"r{i}").ToArray();

    [Fact]
    public async Task ReadsGrowingPagesUntilEnoughMatch()
    {
        var pages = new List<(int Limit, int Offset)>();

        var (matches, truncated) = await FindFilteredFetch.MatchAsync(
            Pager(pages), record => record.EndsWith("7"), key: record => record, needed: 30, budget: 10_000);

        matches.Should().HaveCount(60).And.StartWith(["r7", "r17"]);
        truncated.Should().BeFalse();
        pages.Should().Equal((120, 0), (480, 120));
    }

    [Fact]
    public async Task StopsAtTheBudgetAndSaysSo()
    {
        var pages = new List<(int Limit, int Offset)>();

        var (matches, truncated) = await FindFilteredFetch.MatchAsync(
            Pager(pages), _ => false, key: record => record, needed: 1, budget: 250);

        matches.Should().BeEmpty();
        truncated.Should().BeTrue();
        pages.Should().Equal((100, 0), (150, 100));
    }

    [Fact]
    public async Task AnExhaustedSourceIsNotTruncated()
    {
        var (matches, truncated) = await FindFilteredFetch.MatchAsync(
            Pager([]), _ => true, key: record => record, needed: 5_000, budget: 2_000);

        matches.Should().HaveCount(1_000);
        truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ARecordShiftedIntoTheNextPageIsKeptOnce()
    {
        var (matches, _) = await FindFilteredFetch.MatchAsync(
            (limit, offset) => Task.FromResult<IReadOnlyList<string>>(
                Source.Skip(Math.Max(0, offset - 1)).Take(limit).ToArray()),
            _ => true, key: record => record, needed: 700, budget: 10_000);

        matches.Should().OnlyHaveUniqueItems().And.HaveCount(1_000);
    }

    [Fact]
    public async Task WindowMode_RereadsTheNewestWholeAtEachLargerWindow()
    {
        var windows = new List<int>();

        var (matches, truncated) = await FindFilteredFetch.MatchWindowAsync(
            Newest(windows), record => record.EndsWith("7"), needed: 30, budget: 10_000);

        windows.Should().Equal(120, 480);
        matches.Should().HaveCount(48).And.OnlyHaveUniqueItems();
        truncated.Should().BeFalse();
    }

    [Fact]
    public async Task WindowMode_StopsAtTheBudgetAndSaysSo()
    {
        var windows = new List<int>();

        var (matches, truncated) = await FindFilteredFetch.MatchWindowAsync(
            Newest(windows), _ => false, needed: 1, budget: 250);

        windows.Should().Equal(100, 250);
        matches.Should().BeEmpty();
        truncated.Should().BeTrue();
    }

    [Fact]
    public async Task WindowMode_AnExhaustedSourceIsNotTruncated()
    {
        var windows = new List<int>();

        var (matches, truncated) = await FindFilteredFetch.MatchWindowAsync(
            Newest(windows), _ => true, needed: 5_000, budget: 100_000);

        windows.Should().Equal(20_000);
        matches.Should().HaveCount(1_000);
        truncated.Should().BeFalse();
    }

    private static Func<int, Task<IReadOnlyList<string>>> Newest(List<int> windows) =>
        limit =>
        {
            windows.Add(limit);
            return Task.FromResult<IReadOnlyList<string>>(Source.Take(limit).ToArray());
        };

    private static Func<int, int, Task<IReadOnlyList<string>>> Pager(List<(int Limit, int Offset)> pages) =>
        (limit, offset) =>
        {
            pages.Add((limit, offset));
            return Task.FromResult<IReadOnlyList<string>>(Source.Skip(offset).Take(limit).ToArray());
        };
}
