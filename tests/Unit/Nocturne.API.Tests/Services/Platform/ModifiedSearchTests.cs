using FluentAssertions;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

[Trait("Category", "Unit")]
public class ModifiedSearchTests
{
    [Theory]
    [InlineData("find[srvModified][$gt]=500", 500L)]
    [InlineData("find[srvModified][$gte]=500", 499L)]
    [InlineData("find[srvModified][$gt]=500&find[srvModified][$gte]=900", 899L)]
    [InlineData("{\"srvModified\":{\"$gt\":500}}", 500L)]
    public void ModifiedAfterMills_IsTheStampAServerWriteMustExceed(string find, long expected)
    {
        FindQuery.Parse(find).ModifiedAfterMills.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("find[srvModified][$lt]=500")]
    [InlineData("find[srvModified][$gt]=500&find[eventType]=Note")]
    [InlineData("find[srvModified][$gt]=500&find[created_at][$gte]=2024-01-01")]
    [InlineData("find[eventType]=Note")]
    public void ModifiedAfterMills_IsNullUnlessTheQueryIsOnlyALowerBoundOnSrvModified(string? find)
    {
        FindQuery.Parse(find).ModifiedAfterMills.Should().BeNull();
    }

    [Fact]
    public void CursorFor_UnfilteredAscendingSrvModifiedSortStartsAtTheBeginningOfHistory()
    {
        ModifiedSearch.CursorFor(FindQuery.Empty, ReadSortField.SrvModified, descending: false).Should().Be(0);
        ModifiedSearch.CursorFor(FindQuery.Empty, ReadSortField.SrvModified, descending: true).Should().BeNull();
        ModifiedSearch.CursorFor(FindQuery.Empty, ReadSortField.Time, descending: false).Should().BeNull();
    }

    [Fact]
    public async Task CollectAsync_ResumesAtEachPagesCursor_AndStopsOnceTheNeededRecordsAreHeld()
    {
        var cursors = new List<long>();
        var all = Enumerable.Range(1, 2000).ToList();

        var live = await ModifiedSearch.CollectAsync(
            (cursor, limit, _) =>
            {
                cursors.Add(cursor);
                var page = all.Where(i => i > cursor).Take(limit).ToList();
                return Task.FromResult(new ModifiedSearch.Page<int>(page, page[^1], page.Count < limit));
            },
            i => i % 2 == 0,
            cursor: 0,
            needed: 300,
            maxRead: ModifiedSearch.MaxRead,
            CancellationToken.None);

        live.Should().Equal(Enumerable.Range(1, 1000).Where(i => i % 2 == 1));
        cursors.Should().Equal(0L, 500L);
    }
}
