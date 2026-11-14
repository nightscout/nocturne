using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// What a run reports while it is in flight. Nightscout counts only its last few days for a
/// request with no date filter, so the count has to carry the pull's own, and a source that gives
/// no count at all must not read as finished from its first page.
/// </summary>
public class MigrationProgressTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>Answers by path and query, and records every URL asked for.</summary>
    private sealed class RecordingNightscout(Func<string, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);
            lock (Requests)
                Requests.Add(uri.AbsolutePath + query);
            return Task.FromResult(respond(uri.AbsolutePath, query));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => Json("[]", HttpStatusCode.NotFound);

    private static string Count(long n) => $$"""[{"_id":null,"count":{{n}}}]""";

    private static string FullPage(long newestMills) =>
        "[" + string.Join(",", Enumerable.Range(0, LegacyReadLimits.MaxMergedCount)
            .Select(i => $$"""{"_id":"e{{i}}","date":{{newestMills - i}}}""")) + "]";

    /// <summary>
    /// A source holding a full page of entries and one more, which serves the count it is given.
    /// <paramref name="onSecondPage"/> runs when the run comes back for the second page, the first
    /// moment a page's progress is visible.
    /// </summary>
    private static RecordingNightscout TwoPagesOfEntries(string? count, Action onSecondPage)
    {
        var newest = Start.ToUnixTimeMilliseconds() - 1000;
        var pages = 0;
        return new RecordingNightscout((path, _) =>
        {
            if (path == "/api/v1/count/entries/where")
                return count is null ? NotFound() : Json(count);
            if (path != "/api/v1/entries.json")
                return NotFound();

            if (pages++ == 0)
                return Json(FullPage(newest));

            onSecondPage();
            return Json($$"""[{"_id":"last","date":{{newest - LegacyReadLimits.MaxMergedCount}}}]""");
        });
    }

    [Theory]
    [InlineData("entries", "?find[date][$lte]=")]
    [InlineData("treatments", "?find[created_at][$lte]=")]
    [InlineData("devicestatus", "?find[created_at][$lte]=")]
    public async Task The_count_is_dated_on_the_collections_own_cursor_field(string collection, string filter)
    {
        var handler = new RecordingNightscout((_, _) => NotFound());

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, collection);

        handler.Requests.Should().ContainSingle(r => r.StartsWith($"/api/v1/count/{collection}/where"))
            .Which.Should().Contain(filter, "an undated count is a count of Nightscout's last four days");
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("food")]
    [InlineData("activity")]
    [InlineData("subjects")]
    public async Task A_collection_Nightscout_cannot_count_is_not_asked_for_one(string collection)
    {
        var handler = new RecordingNightscout((_, _) => NotFound());

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, collection);

        handler.Requests.Should().NotContain(r => r.StartsWith("/api/v1/count/"),
            "Nightscout answers a count of any other collection with its entries");
    }

    [Fact]
    public async Task A_collection_in_flight_is_measured_against_the_count_and_a_finished_one_holds_what_was_served()
    {
        MigrationJob? running = null;
        MigrationJobStatus? midRun = null;
        var handler = TwoPagesOfEntries(Count(25_000), () => midRun = running!.GetStatus());

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(
            provider, job => running = job, ["entries"], time: new FakeTimeProvider(Start));

        var inFlight = midRun!.CollectionProgress["entries"];
        inFlight.TotalDocuments.Should().Be(25_000);
        inFlight.DocumentsMigrated.Should().Be(LegacyReadLimits.MaxMergedCount);
        inFlight.ProgressPercentage.Should().Be(40);
        midRun.ProgressPercentage.Should().Be(40);

        var finished = status.CollectionProgress["entries"];
        finished.TotalDocuments.Should().Be(LegacyReadLimits.MaxMergedCount + 1);
        finished.DocumentsMigrated.Should().Be(LegacyReadLimits.MaxMergedCount + 1);
        status.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public async Task A_source_that_gives_no_count_does_not_read_as_finished_while_it_is_still_being_pulled()
    {
        MigrationJob? running = null;
        MigrationJobStatus? midRun = null;
        var handler = TwoPagesOfEntries(count: null, () => midRun = running!.GetStatus());

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(
            provider, job => running = job, ["entries"], time: new FakeTimeProvider(Start));

        var inFlight = midRun!.CollectionProgress["entries"];
        inFlight.TotalDocuments.Should().Be(0, "the total is not known until the pull ends");
        inFlight.DocumentsMigrated.Should().Be(LegacyReadLimits.MaxMergedCount);
        midRun.ProgressPercentage.Should().Be(0, "no collection has finished");
        midRun.EstimatedTimeRemaining.Should().BeNull();

        status.CollectionProgress["entries"].TotalDocuments.Should().Be(LegacyReadLimits.MaxMergedCount + 1);
        status.ProgressPercentage.Should().Be(100);
    }

    /// <summary>
    /// A collection weighed at what it has served so far reads as nearly done for as long as it is
    /// pulled, so one count the source did not give takes the whole run off document weighting.
    /// </summary>
    [Fact]
    public async Task A_run_missing_one_count_steps_by_collection_and_estimates_nothing()
    {
        var time = new FakeTimeProvider(Start);
        var newest = Start.ToUnixTimeMilliseconds() - 1000;
        MigrationJob? running = null;
        MigrationJobStatus? midRun = null;
        var pages = 0;
        var handler = new RecordingNightscout((path, _) =>
        {
            switch (path)
            {
                case "/api/v1/count/entries/where":
                    return Json(Count(25_000));
                case "/api/v1/entries.json" when pages++ == 0:
                    return Json(FullPage(newest));
                case "/api/v1/entries.json":
                    time.Advance(TimeSpan.FromMinutes(2));
                    midRun = running!.GetStatus();
                    return Json("[]");
                default:
                    return NotFound();
            }
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, job => running = job, ["entries", "treatments"], time: time);

        handler.Requests.Should().Contain(r => r.StartsWith("/api/v1/count/treatments/where"));
        midRun!.ProgressPercentage.Should().Be(0, "neither collection has finished");
        midRun.EstimatedTimeRemaining.Should().BeNull();
        midRun.CollectionProgress["entries"].TotalDocuments.Should().Be(25_000);
    }

    /// <summary>An empty collection is counted, as nothing; it is not a count the source withheld.</summary>
    [Fact]
    public async Task A_collection_counted_as_empty_keeps_the_run_measured_in_documents()
    {
        MigrationJob? running = null;
        MigrationJobStatus? midRun = null;
        var newest = Start.ToUnixTimeMilliseconds() - 1000;
        var pages = 0;
        var handler = new RecordingNightscout((path, _) =>
        {
            switch (path)
            {
                case "/api/v1/count/entries/where":
                    return Json(Count(25_000));
                case "/api/v1/count/treatments/where":
                    return Json("[]");
                case "/api/v1/entries.json" when pages++ == 0:
                    return Json(FullPage(newest));
                case "/api/v1/entries.json":
                    midRun = running!.GetStatus();
                    return Json("[]");
                case "/api/v1/treatments.json":
                    return Json("[]");
                default:
                    return NotFound();
            }
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(
            provider, job => running = job, ["entries", "treatments"], time: new FakeTimeProvider(Start));

        midRun!.ProgressPercentage.Should().Be(40);
    }

    [Fact]
    public async Task A_run_in_flight_estimates_the_time_remaining_from_the_share_of_documents_done()
    {
        var time = new FakeTimeProvider(Start);
        MigrationJob? running = null;
        MigrationJobStatus? midRun = null;
        var handler = TwoPagesOfEntries(Count(25_000), () =>
        {
            time.Advance(TimeSpan.FromMinutes(2));
            midRun = running!.GetStatus();
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, job => running = job, ["entries"], time: time);

        midRun!.EstimatedTimeRemaining.Should().Be(TimeSpan.FromMinutes(3), "40% took two minutes");
        status.EstimatedTimeRemaining.Should().BeNull("the run has ended");
    }

    [Fact]
    public async Task A_date_range_bounds_the_count_and_the_pull_and_keeps_out_what_falls_outside_it()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var inside = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var before = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var handler = new RecordingNightscout((path, _) => path switch
        {
            "/api/v1/count/entries/where" => Json(Count(2)),
            "/api/v1/entries.json" => Json($$"""[{"_id":"a","date":{{inside}}},{"_id":"b","date":{{before}}}]"""),
            _ => NotFound(),
        });

        var imported = new List<Entry>();
        await using var provider = MigrationJobHarness.BuildProvider(handler, entryOutcome: page =>
        {
            imported.AddRange(page);
            return new();
        });
        var status = await MigrationJobHarness.RunAsync(
            provider, onCreated: null, ["entries"], time: new FakeTimeProvider(Start), startDate: from, endDate: to);

        var window = $"find[date][$lte]={new DateTimeOffset(to).ToUnixTimeMilliseconds()}"
            + $"&find[date][$gte]={new DateTimeOffset(from).ToUnixTimeMilliseconds()}";
        handler.Requests.Should().Contain($"/api/v1/count/entries/where?{window}");
        handler.Requests.Should().Contain($"/api/v1/entries.json?count={LegacyReadLimits.MaxMergedCount}&{window}");

        imported.Should().ContainSingle().Which.Id.Should().Be("a");
        status.CollectionProgress["entries"].TotalDocuments.Should().Be(1);
    }

    [Fact]
    public async Task An_end_date_in_the_future_is_read_as_now()
    {
        var handler = new RecordingNightscout((_, _) => NotFound());

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(
            provider, onCreated: null, ["entries"], time: new FakeTimeProvider(Start),
            endDate: Start.UtcDateTime.AddYears(1));

        handler.Requests.Should().Contain(
            $"/api/v1/count/entries/where?find[date][$lte]={Start.ToUnixTimeMilliseconds()}");
    }

    /// <summary>
    /// Nightscout's food route takes no paging, so a list of a full page or more is served again
    /// for every <c>skip</c>, whether or not its foods carry an id.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_food_list_served_whole_for_every_skip_is_imported_once_and_ends(bool withIds)
    {
        var foods = "[" + string.Join(",", Enumerable.Range(0, LegacyReadLimits.MaxMergedCount)
            .Select(i => withIds
                ? $$"""{"_id":"f{{i}}","name":"Food {{i}}","type":"food"}"""
                : $$"""{"name":"Food {{i}}","type":"food"}""")) + "]";
        var handler = new RecordingNightscout((path, _) => path == "/api/v1/food.json" ? Json(foods) : NotFound());

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "food").WaitAsync(TimeSpan.FromMinutes(2));

        handler.Requests.Count(r => r.StartsWith("/api/v1/food.json")).Should().Be(2);
        var food = status.CollectionProgress["food"];
        food.DocumentsMigrated.Should().Be(LegacyReadLimits.MaxMergedCount);
        food.TotalDocuments.Should().Be(LegacyReadLimits.MaxMergedCount);
    }
}
