using System.Net;
using System.Text;
using FluentAssertions;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// The request sequence an API-mode migration issues while pulling a collection page by page.
/// Every page is explicitly dated and asks for <see cref="LegacyReadLimits.MaxMergedCount"/>
/// records: a larger page is clamped by the merged v1 reads, and since a short page ends the pull,
/// a clamped first page would import one page and report the collection complete. The cursor field
/// differs per collection (entries carry a numeric <c>date</c>, everything else an ISO
/// <c>created_at</c>), so each has to page back on its own field or the second request repeats the
/// first page.
/// </summary>
public class MigrationPagedPullTests
{
    /// <summary>
    /// Stands in for the source Nightscout instance, serving one queued response per request to a
    /// single collection route and recording the URLs asked for. Everything else — including the
    /// count endpoint the migration probes first — answers 404.
    /// </summary>
    private sealed class NightscoutPager(string route, Queue<(HttpStatusCode Status, string Body)> pages)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath != route)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            Requests.Add(Uri.UnescapeDataString(uri.PathAndQuery));

            var (status, body) = pages.Count > 0 ? pages.Dequeue() : (HttpStatusCode.OK, "[]");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string FullPage(Func<int, string> record) =>
        "[" + string.Join(",", Enumerable.Range(0, LegacyReadLimits.MaxMergedCount).Select(record)) + "]";

    [Theory]
    [InlineData("entries", "find[date][$lte]=")]
    [InlineData("treatments", "find[created_at][$lte]=")]
    [InlineData("devicestatus", "find[created_at][$lte]=")]
    [InlineData("activity", "find[created_at][$lte]=")]
    public async Task A_pull_asks_for_a_full_page_and_dates_it_on_the_collections_own_cursor_field(
        string collection, string cursorField)
    {
        var handler = new NightscoutPager(
            $"/api/v1/{collection}.json",
            new Queue<(HttpStatusCode, string)>([(HttpStatusCode.OK, """[{ }]""")]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, collection);

        handler.Requests.Should().ContainSingle()
            .Which.Should().Contain($"count={LegacyReadLimits.MaxMergedCount}&")
            .And.Contain(cursorField);
    }

    [Fact]
    public async Task Entries_page_back_from_the_oldest_date_on_the_page()
    {
        var oldestMs = DateTimeOffset.Parse("2026-02-01T00:00:00Z").ToUnixTimeMilliseconds();
        var handler = new NightscoutPager(
            "/api/v1/entries.json",
            new Queue<(HttpStatusCode, string)>([
                (HttpStatusCode.OK, FullPage(i => $$"""{"date":{{oldestMs + i}}}""")),
                (HttpStatusCode.OK, """[{ }]"""),
            ]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, "entries");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain($"find[date][$lte]={oldestMs - 1}");
    }

    [Fact]
    public async Task Created_at_collections_page_back_from_the_oldest_timestamp_on_the_page()
    {
        var oldest = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        var handler = new NightscoutPager(
            "/api/v1/treatments.json",
            new Queue<(HttpStatusCode, string)>([
                (HttpStatusCode.OK, FullPage(i =>
                    $$"""{"created_at":"{{oldest.AddMinutes(i):yyyy-MM-ddTHH:mm:ss.fffZ}}"}""")),
                (HttpStatusCode.OK, """[{ }]"""),
            ]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, "treatments");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain(
            $"find[created_at][$lte]={oldest.UtcDateTime.AddMilliseconds(-1):o}");
    }

    [Fact]
    public async Task A_failed_fetch_ends_the_pull_after_one_request()
    {
        var handler = new NightscoutPager(
            "/api/v1/entries.json",
            new Queue<(HttpStatusCode, string)>([(HttpStatusCode.InternalServerError, "")]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        handler.Requests.Should().ContainSingle();
        status.CollectionProgress["entries"].FailureReason.Should().NotBeNull();
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_is_counted_failed_and_the_rest_of_the_collection_still_migrates()
    {
        var oldestMs = DateTimeOffset.Parse("2026-02-01T00:00:00Z").ToUnixTimeMilliseconds();
        var handler = new NightscoutPager(
            "/api/v1/entries.json",
            new Queue<(HttpStatusCode, string)>([
                (HttpStatusCode.OK, FullPage(i => i == 5
                    ? $$"""{"date":{{oldestMs + i}},"device":42}"""
                    : $$"""{"date":{{oldestMs + i}}}""")),
                (HttpStatusCode.OK, $$"""[{"date":{{oldestMs - 10}}},{"date":{{oldestMs - 20}}}]"""),
            ]));
        var decomposedPageSizes = new List<int>();

        await using var provider = MigrationJobHarness.BuildProvider(handler, entryOutcome: page =>
        {
            decomposedPageSizes.Add(page.Count);
            return new DecompositionResult();
        });
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain($"find[date][$lte]={oldestMs - 1}");
        decomposedPageSizes.Should().Equal(LegacyReadLimits.MaxMergedCount - 1, 2);

        var entries = status.CollectionProgress["entries"];
        entries.DocumentsFailed.Should().Be(1);
        entries.DocumentsMigrated.Should().Be(LegacyReadLimits.MaxMergedCount - 1 + 2);
        entries.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task A_full_page_with_no_readable_document_fails_the_collection_rather_than_paging_forever()
    {
        var handler = new NightscoutPager(
            "/api/v1/entries.json",
            new Queue<(HttpStatusCode, string)>([
                (HttpStatusCode.OK, FullPage(i => $$"""{"date":{{1770000000000 + i}},"device":[]}""")),
            ]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        handler.Requests.Should().ContainSingle();
        var entries = status.CollectionProgress["entries"];
        entries.DocumentsFailed.Should().Be(LegacyReadLimits.MaxMergedCount);
        entries.FailureReason.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unreadable_document_fetched_again_on_the_next_page_is_counted_failed_once()
    {
        var oldestMs = DateTimeOffset.Parse("2026-02-01T00:00:00Z").ToUnixTimeMilliseconds();
        var unreadable = $$"""{"_id":"bad","date":{{oldestMs}},"device":42}""";
        var handler = new NightscoutPager(
            "/api/v1/entries.json",
            new Queue<(HttpStatusCode, string)>([
                (HttpStatusCode.OK, FullPage(i => i == 0 ? unreadable : $$"""{"date":{{oldestMs + i}}}""")),
                (HttpStatusCode.OK, $$"""[{{unreadable}},{"date":{{oldestMs - 10}}}]"""),
            ]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        handler.Requests.Should().HaveCount(2);
        var entries = status.CollectionProgress["entries"];
        entries.DocumentsFailed.Should().Be(1);
        entries.DocumentsMigrated.Should().Be(LegacyReadLimits.MaxMergedCount);
    }

    [Fact]
    public async Task A_full_page_with_no_date_to_page_back_from_fails_the_collection_rather_than_ending_quietly()
    {
        var handler = new NightscoutPager(
            "/api/v1/entries.json",
            new Queue<(HttpStatusCode, string)>([(HttpStatusCode.OK, FullPage(_ => "{ }"))]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        handler.Requests.Should().ContainSingle();
        var entries = status.CollectionProgress["entries"];
        entries.DocumentsMigrated.Should().Be(LegacyReadLimits.MaxMergedCount);
        entries.FailureReason.Should().NotBeNull();
    }
}
