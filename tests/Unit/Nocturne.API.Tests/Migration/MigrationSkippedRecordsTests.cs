using System.Net;
using System.Text;
using FluentAssertions;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A page the decomposer only partly stored must not be counted as fully migrated. The person who
/// deleted a stretch of readings and then re-imported it has to be told those readings stayed away.
/// </summary>
public class MigrationSkippedRecordsTests
{
    private sealed class RoutedNightscout(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body = "[]") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static RoutedNightscout ThreeDocuments(string collection) => new(path =>
        path == $"/api/v1/{collection}.json"
            ? Json(HttpStatusCode.OK,
                """[{"date":1770000000000,"created_at":"2026-02-02T02:40:00Z"},{"date":1770000060000,"created_at":"2026-02-02T02:41:00Z"},{"date":1770000120000,"created_at":"2026-02-02T02:42:00Z"}]""")
            : Json(HttpStatusCode.NotFound));

    private static DecompositionResult Stored(int records, int skippedDeleted = 0, int skippedUnsupported = 0)
    {
        var result = new DecompositionResult { SkippedDeleted = skippedDeleted, SkippedUnsupported = skippedUnsupported };
        for (var i = 0; i < records; i++)
            result.CreatedRecords.Add(new SensorGlucose());
        return result;
    }

    /// <summary>
    /// An entry becomes exactly one record, so one the user deleted is a document nothing was stored
    /// for, and it must not count as migrated.
    /// </summary>
    [Fact]
    public async Task Entries_count_only_what_was_stored_as_migrated()
    {
        await using var provider = MigrationJobHarness.BuildProvider(
            ThreeDocuments("entries"),
            entryOutcome: _ => Stored(records: 1, skippedDeleted: 1, skippedUnsupported: 1));

        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        var entries = status.CollectionProgress["entries"];
        entries.DocumentsMigrated.Should().Be(1, "of three entries one was deleted and one is not stored by Nocturne");
        entries.RecordsStored.Should().Be(1);
        entries.DocumentsSkippedDeleted.Should().Be(1);
        entries.RecordsSkippedDeleted.Should().Be(1);
        entries.DocumentsSkippedUnsupported.Should().Be(1);
        entries.ProgressPercentage.Should().Be(100, "every document was dealt with");
        status.State.Should().Be(MigrationJobState.Completed);
    }

    /// <summary>
    /// A treatment can become several records, so a deleted record cannot be pinned to a document.
    /// The stored and skipped records are reported instead of claiming more documents than landed.
    /// </summary>
    [Fact]
    public async Task Treatments_report_stored_and_skipped_records_beside_the_documents()
    {
        await using var provider = MigrationJobHarness.BuildProvider(
            ThreeDocuments("treatments"),
            treatmentOutcome: _ => Stored(records: 4, skippedDeleted: 2));

        var status = await MigrationJobHarness.RunAsync(provider, "treatments");

        var treatments = status.CollectionProgress["treatments"];
        treatments.RecordsStored.Should().Be(4);
        treatments.RecordsSkippedDeleted.Should().Be(2);
        treatments.DocumentsSkippedDeleted.Should().Be(0);
        treatments.DocumentsMigrated.Should().Be(3);
        treatments.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public async Task A_page_stored_in_full_reports_nothing_skipped()
    {
        await using var provider = MigrationJobHarness.BuildProvider(
            ThreeDocuments("entries"), entryOutcome: _ => Stored(records: 3));

        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        var entries = status.CollectionProgress["entries"];
        entries.DocumentsMigrated.Should().Be(3);
        entries.RecordsStored.Should().Be(3);
        entries.DocumentsSkippedUnsupported.Should().Be(0);
        entries.RecordsSkippedDeleted.Should().Be(0);
    }
}
