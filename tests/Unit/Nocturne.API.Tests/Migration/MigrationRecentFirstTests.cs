using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// An API run reads the newest <see cref="MigrationJob.RecentSpan"/> of every collection before the
/// older history of any, and says when that part is in, so the app can be used while years of
/// history are still arriving.
/// </summary>
public class MigrationRecentFirstTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset Split = Start - MigrationJob.RecentSpan;

    /// <summary>A Nightscout that honours the date filter on entries and treatments, and records what was asked.</summary>
    private sealed class DatedNightscout(
        IReadOnlyList<DateTimeOffset> entries,
        IReadOnlyList<DateTimeOffset> treatments,
        Action<string>? onRequest = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool FailTreatments { get; init; }

        /// <summary>The treatments request, counted from 1, at which the host stops answering; 0 for never.</summary>
        public int UnreachableFromTreatmentsRequest { get; init; }

        private int _treatmentRequests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);
            Requests.Add(uri.AbsolutePath + query);
            onRequest?.Invoke(uri.AbsolutePath + query);

            if (uri.AbsolutePath == "/api/v1/treatments.json"
                && UnreachableFromTreatmentsRequest > 0
                && ++_treatmentRequests >= UnreachableFromTreatmentsRequest)
            {
                throw new HttpRequestException("connection refused");
            }

            return Task.FromResult(uri.AbsolutePath switch
            {
                "/api/v1/entries.json" => Json(Array(Within(entries, query, "date").Select(at =>
                    $$"""{"_id":"e{{at.ToUnixTimeMilliseconds()}}","date":{{at.ToUnixTimeMilliseconds()}}}"""))),
                "/api/v1/treatments.json" when FailTreatments => Json("[]", HttpStatusCode.InternalServerError),
                "/api/v1/treatments.json" => Json(Array(Within(treatments, query, "created_at").Select(at =>
                    $$"""{"_id":"t{{at.ToUnixTimeMilliseconds()}}","created_at":"{{at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}}"}"""))),
                "/api/v1/devicestatus.json" => Json("[]"),
                "/api/v1/profile.json" => Json("""[{"_id":"p1","defaultProfile":"Default"}]"""),
                _ => Json("[]", HttpStatusCode.NotFound),
            });
        }

        private static string Array(IEnumerable<string> documents) => "[" + string.Join(",", documents) + "]";

        private static IEnumerable<DateTimeOffset> Within(IReadOnlyList<DateTimeOffset> records, string query, string field)
        {
            var upper = Bound(query, $"find[{field}][$lte]=");
            var lower = Bound(query, $"find[{field}][$gte]=");
            return records
                .Where(at => (upper is null || at <= upper) && (lower is null || at >= lower))
                .OrderByDescending(at => at);
        }

        private static DateTimeOffset? Bound(string query, string marker)
        {
            var at = query.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return null;

            var value = query[(at + marker.Length)..].Split('&')[0];
            return long.TryParse(value, out var mills)
                ? DateTimeOffset.FromUnixTimeMilliseconds(mills)
                : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static Task<MigrationJobStatus> RunAsync(
        IServiceProvider provider, string[] collections, Action<MigrationJob>? onCreated = null,
        DateTime? startDate = null) =>
        MigrationJobHarness.RunAsync(
            provider, onCreated, collections, time: new FakeTimeProvider(Start), startDate: startDate,
            readFirst: MigrationJob.RecentSpan);

    private static string EntryId(DateTimeOffset at) => $"e{at.ToUnixTimeMilliseconds()}";

    private static string TreatmentId(DateTimeOffset at) => $"t{at.ToUnixTimeMilliseconds()}";

    [Fact]
    public async Task The_recent_part_of_every_collection_is_read_before_the_older_history_of_any()
    {
        var handler = new DatedNightscout(
            entries: [Start.AddDays(-1), Start.AddDays(-400)],
            treatments: [Start.AddDays(-2), Start.AddDays(-300)]);

        var imported = new List<string>();
        await using var provider = MigrationJobHarness.BuildProvider(
            handler,
            entryOutcome: page =>
            {
                imported.AddRange(page.Select(e => e.Id!));
                return new();
            },
            treatmentOutcome: page =>
            {
                imported.AddRange(page.Select(t => t.Id!));
                return new();
            },
            profileOutcome: _ =>
            {
                imported.Add("profile");
                return new();
            });

        var status = await RunAsync(provider, ["entries", "treatments", "profile"]);

        imported.Should().Equal(
            EntryId(Start.AddDays(-1)),
            TreatmentId(Start.AddDays(-2)),
            "profile",
            EntryId(Start.AddDays(-400)),
            TreatmentId(Start.AddDays(-300)));

        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(2);
        status.CollectionProgress["treatments"].DocumentsMigrated.Should().Be(2);
        handler.Requests.Count(r => r.StartsWith("/api/v1/profile.json")).Should()
            .Be(1, "a profile holds no time to split on");
    }

    [Fact]
    public async Task The_run_says_its_recent_data_is_ready_only_once_every_collection_has_given_it()
    {
        MigrationJob? running = null;
        var pulls = new List<(bool Ready, bool EntriesComplete)>();
        var handler = new DatedNightscout(
            entries: [Start.AddDays(-1), Start.AddDays(-400)],
            treatments: [Start.AddDays(-2)],
            onRequest: request =>
            {
                if (!request.StartsWith("/api/v1/entries.json") && !request.StartsWith("/api/v1/treatments.json"))
                    return;

                var status = running!.GetStatus();
                pulls.Add((status.RecentDataReady,
                    status.CollectionProgress.TryGetValue("entries", out var entries) && entries.IsComplete));
            });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var final = await RunAsync(provider, ["entries", "treatments"], job => running = job);

        pulls.Select(p => p.Ready).Should().Equal(false, false, true, true);
        pulls[2].EntriesComplete.Should().BeFalse("its older history is still to come");
        final.RecentDataReady.Should().BeTrue();
    }

    /// <summary>
    /// The app is handed the run on this signal and tells the user their recent data is ready. A
    /// collection that failed has none, so the run keeps the signal back until it ends and reports
    /// the failure.
    /// </summary>
    [Fact]
    public async Task A_run_whose_recent_part_lost_a_collection_does_not_say_its_recent_data_is_ready()
    {
        MigrationJob? running = null;
        var readyDuringHistory = new List<bool>();
        var entryPulls = 0;
        var handler = new DatedNightscout(
            entries: [Start.AddDays(-1), Start.AddDays(-400)],
            treatments: [],
            onRequest: request =>
            {
                if (request.StartsWith("/api/v1/entries.json") && ++entryPulls == 2)
                    readyDuringHistory.Add(running!.GetStatus().RecentDataReady);
            })
        {
            FailTreatments = true,
        };

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var final = await RunAsync(provider, ["entries", "treatments"], job => running = job);

        readyDuringHistory.Should().Equal(false);
        final.State.Should().Be(MigrationJobState.Completed);
        final.HasFailures.Should().BeTrue();
    }

    /// <summary>
    /// A connection that fails in the older history leaves collections holding their recent part.
    /// Each says its older history is missing; none is reported as not attempted.
    /// </summary>
    [Fact]
    public async Task A_host_that_dies_in_the_older_history_names_what_each_collection_is_missing()
    {
        var handler = new DatedNightscout(
            entries: [Start.AddDays(-1), Start.AddDays(-400)],
            treatments: [Start.AddDays(-2), Start.AddDays(-300)])
        {
            UnreachableFromTreatmentsRequest = 2,
        };

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await RunAsync(provider, ["entries", "treatments", "devicestatus"]);

        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["entries"].FailureReason.Should().BeNull("both its parts were read");
        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(2);
        status.CollectionProgress["treatments"].DocumentsMigrated.Should().Be(1);
        status.CollectionProgress["devicestatus"].FailureReason.Should()
            .Be("The older history was not imported because the connection to Nightscout had already failed.");
        status.ErrorMessage.Should().NotContain("not attempted");
    }

    [Fact]
    public async Task A_record_on_the_boundary_between_the_parts_is_imported_once()
    {
        var handler = new DatedNightscout(entries: [Split], treatments: [Split]);

        var entries = new List<Entry>();
        var treatments = new List<Treatment>();
        await using var provider = MigrationJobHarness.BuildProvider(
            handler,
            entryOutcome: page =>
            {
                entries.AddRange(page);
                return new();
            },
            treatmentOutcome: page =>
            {
                treatments.AddRange(page);
                return new();
            });

        var status = await RunAsync(provider, ["entries", "treatments"]);

        entries.Should().ContainSingle();
        treatments.Should().ContainSingle();
        status.CollectionProgress["entries"].TotalDocuments.Should().Be(1);
        status.CollectionProgress["treatments"].TotalDocuments.Should().Be(1);
    }

    [Fact]
    public async Task A_range_no_longer_than_the_recent_span_is_read_once()
    {
        var handler = new DatedNightscout(entries: [Start.AddDays(-1)], treatments: []);

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await RunAsync(provider, ["entries"], startDate: Start.UtcDateTime.AddDays(-7));

        handler.Requests.Count(r => r.StartsWith("/api/v1/entries.json")).Should().Be(1);
    }

    /// <summary>
    /// A source with nothing recent imports nothing in the first part. That is not the run that
    /// has nothing to keep: the source answered, so one collection failing after it is reported
    /// against that collection and the history still arrives.
    /// </summary>
    [Fact]
    public async Task A_collection_failing_on_a_source_with_nothing_recent_does_not_fail_the_run()
    {
        var handler = new DatedNightscout(entries: [Start.AddDays(-400)], treatments: []) { FailTreatments = true };

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await RunAsync(provider, ["entries", "treatments"]);

        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(1);
        status.CollectionProgress["treatments"].FailureReason.Should().NotBeNull();
        handler.Requests.Count(r => r.StartsWith("/api/v1/treatments.json")).Should()
            .Be(1, "a failed collection is not read again");
    }
}
