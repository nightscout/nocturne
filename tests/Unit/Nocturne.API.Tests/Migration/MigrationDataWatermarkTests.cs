using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// What a completed run leaves in <see cref="MigrationSourceEntity.LastMigratedDataTimestamp"/>.
/// The watermark answers "what is already imported", so it has to be the newest record the run
/// actually brought across. It also has to survive a later run that covers older ground.
/// </summary>
public class MigrationDataWatermarkTests
{
    /// <summary>
    /// Serves one queued body per request to a single collection route, 404 for everything else
    /// (including the count probe). An exhausted queue answers an empty page, which ends a pull.
    /// </summary>
    private sealed class NightscoutSource(string route, Queue<string> bodies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != route)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var body = bodies.Count > 0 ? bodies.Dequeue() : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FailingSource : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private static async Task<MigrationSourceEntity> SourceAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        return await db.MigrationSources.SingleAsync();
    }

    [Fact]
    public async Task A_completed_entries_pull_records_the_newest_entry_date()
    {
        var oldest = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        var newest = DateTimeOffset.Parse("2026-02-03T09:30:00Z");
        var handler = new NightscoutSource(
            "/api/v1/entries.json",
            new Queue<string>([
                $$"""
                [{"date":{{oldest.ToUnixTimeMilliseconds()}}},{"date":{{newest.ToUnixTimeMilliseconds()}}}]
                """,
            ]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, "entries");

        (await SourceAsync(provider)).LastMigratedDataTimestamp
            .Should().Be(newest.UtcDateTime);
    }

    [Fact]
    public async Task A_completed_created_at_pull_records_the_newest_timestamp()
    {
        var oldest = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        var newest = DateTimeOffset.Parse("2026-02-04T18:15:00Z");
        var handler = new NightscoutSource(
            "/api/v1/treatments.json",
            new Queue<string>([
                $$"""
                [{"created_at":"{{oldest:yyyy-MM-ddTHH:mm:ss.fffZ}}"},{"created_at":"{{newest:yyyy-MM-ddTHH:mm:ss.fffZ}}"}]
                """,
            ]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        await MigrationJobHarness.RunAsync(provider, "treatments");

        (await SourceAsync(provider)).LastMigratedDataTimestamp
            .Should().Be(newest.UtcDateTime);
    }

    [Fact]
    public async Task A_later_run_over_older_data_leaves_the_watermark_where_it_was()
    {
        var newest = DateTimeOffset.Parse("2026-02-03T09:30:00Z");
        var older = DateTimeOffset.Parse("2026-01-05T04:00:00Z");
        var handler = new NightscoutSource(
            "/api/v1/entries.json",
            new Queue<string>([
                $$"""[{"date":{{newest.ToUnixTimeMilliseconds()}}}]""",
                $$"""[{"date":{{older.ToUnixTimeMilliseconds()}}}]""",
            ]));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var owner = MigrationJobHarness.NewTenant();

        await MigrationJobHarness.RunAsync(provider, onCreated: null, ["entries"], owner: owner);
        await MigrationJobHarness.RunAsync(provider, onCreated: null, ["entries"], owner: owner);

        (await SourceAsync(provider)).LastMigratedDataTimestamp
            .Should().Be(newest.UtcDateTime);
    }

    [Fact]
    public async Task A_run_that_imported_nothing_records_no_watermark()
    {
        await using var provider = MigrationJobHarness.BuildProvider(new FailingSource());
        await MigrationJobHarness.RunAsync(provider, "entries");

        (await SourceAsync(provider)).LastMigratedDataTimestamp.Should().BeNull();
    }
}
