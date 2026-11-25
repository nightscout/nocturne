using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// Runs <c>ShowNightscoutSourcesByHost</c> on a real PostgreSQL as the migrator over API-mode
/// migration sources stored in the masked form (a long cut segment, a short kept one behind a port,
/// a bare host, an IPv6 host with a port, a legacy path after a backslash) and a MongoDB source, then
/// runs the rewrite a second time. Two of the runs hold a description that is not their source's URL.
/// </summary>
public class ShowNightscoutSourcesByHostFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261125060400_AddStateSpanUpstreamFingerprint";

    internal const string LongPathMasked = "ns.example.com/AGYL…/x";
    internal const string ShortPathMasked = "ns.other.example:8443/short";
    internal const string BareHost = "bare.example";
    internal const string Ipv6Masked = "[::1]:8080/x";
    internal const string BackslashMasked = @"ns.backslash.example\s3cr3t";
    internal const string OtherDescription = "recorded before the rewrite";
    internal const string MongoUrl = "legacy.example/kept";
    internal const string MongoDescription = "MongoDB: nightscout/db";

    internal static readonly Guid Tenant = Guid.Parse("b1111111-1111-7111-8111-111111114171");

    internal static readonly Guid LongPath = Guid.Parse("c1111111-1111-7111-8111-111111114171");
    internal static readonly Guid ShortPath = Guid.Parse("c2222222-2222-7222-8222-222222224171");
    internal static readonly Guid Bare = Guid.Parse("c3333333-3333-7333-8333-333333334171");
    internal static readonly Guid Mongo = Guid.Parse("c4444444-4444-7444-8444-444444444171");
    internal static readonly Guid Ipv6 = Guid.Parse("c5555555-5555-7555-8555-555555554171");
    internal static readonly Guid Backslash = Guid.Parse("c6666666-6666-7666-8666-666666664171");

    private string _migrator = string.Empty;

    internal IReadOnlyList<SourceRow> SourcesBefore { get; private set; } = [];

    internal IReadOnlyList<SourceRow> Sources { get; private set; } = [];

    internal IReadOnlyList<RunRow> Runs { get; private set; } = [];

    internal IReadOnlyList<SourceRow> SourcesAfterSecondRun { get; private set; } = [];

    internal IReadOnlyList<RunRow> RunsAfterSecondRun { get; private set; } = [];

    internal sealed record SourceRow(Guid Id, string Identifier, string? Url);

    internal sealed record RunRow(Guid Id, Guid SourceId, string? Description);

    internal static string Digest(Guid source) => source.ToString("N") + source.ToString("N");

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "show_nightscout_sources");
        _migrator = database.MigratorConnectionString;

        await ExecuteAsync($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{Tenant}', 'show-{Tenant:N}', 'show', true, now(), now());
            """);

        foreach (var (id, mode, url, description) in new[]
                 {
                     (LongPath, "Api", LongPathMasked, LongPathMasked),
                     (ShortPath, "Api", ShortPathMasked, ShortPathMasked),
                     (Bare, "Api", BareHost, BareHost),
                     (Ipv6, "Api", Ipv6Masked, OtherDescription),
                     (Backslash, "Api", BackslashMasked, OtherDescription),
                     (Mongo, "MongoDb", MongoUrl, MongoDescription),
                 })
        {
            await ExecuteAsync($"""
                INSERT INTO migration_sources (id, tenant_id, mode, source_identifier, nightscout_url, created_at)
                VALUES ('{id}', '{Tenant}', '{mode}', '{Digest(id)}', '{url}', now());
                INSERT INTO migration_runs (id, source_id, tenant_id, mode, state, started_at, created_at,
                    entries_migrated, treatments_migrated, revision, source_description)
                VALUES ('{Guid.CreateVersion7()}', '{id}', '{Tenant}', '{mode}', 'Completed', now(), now(), 0, 0, 0,
                    '{description}');
                """);
        }

        SourcesBefore = await ReadSourcesAsync();

        await using (var context = new NocturneDbContext(Options(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync();

        Sources = await ReadSourcesAsync();
        Runs = await ReadRunsAsync();
        await ExecuteAsync(ShowNightscoutSourcesByHost.RewriteSql);
        SourcesAfterSecondRun = await ReadSourcesAsync();
        RunsAfterSecondRun = await ReadRunsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static DbContextOptions<NocturneDbContext> Options(string connectionString) =>
        TestContexts.MigrationsOptions(connectionString);

    private async Task<IReadOnlyList<SourceRow>> ReadSourcesAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, source_identifier, nightscout_url FROM migration_sources", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<SourceRow>();
        while (await reader.ReadAsync())
            rows.Add(new SourceRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    private async Task<IReadOnlyList<RunRow>> ReadRunsAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, source_id, source_description FROM migration_runs", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<RunRow>();
        while (await reader.ReadAsync())
            rows.Add(new RunRow(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Integration")]
public class ShowNightscoutSourcesByHostTests(ShowNightscoutSourcesByHostFixture fixture)
    : IClassFixture<ShowNightscoutSourcesByHostFixture>
{
    [Theory]
    [InlineData("c1111111-1111-7111-8111-111111114171", "ns.example.com/…")]
    [InlineData("c2222222-2222-7222-8222-222222224171", "ns.other.example:8443/…")]
    [InlineData("c3333333-3333-7333-8333-333333334171", ShowNightscoutSourcesByHostFixture.BareHost)]
    [InlineData("c5555555-5555-7555-8555-555555554171", "[::1]:8080/…")]
    [InlineData("c6666666-6666-7666-8666-666666664171", "ns.backslash.example/…")]
    public void An_api_source_and_its_runs_read_as_its_host_with_no_path(string id, string shown)
    {
        var source = Guid.Parse(id);

        fixture.Sources.Single(s => s.Id == source).Url.Should().Be(shown);
        fixture.Runs.Single(r => r.SourceId == source).Description.Should().Be(shown);
    }

    [Theory]
    [InlineData("https://ns.example.com/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI/x?token=t")]
    [InlineData("https://ns.other.example:8443/short")]
    [InlineData("https://bare.example/")]
    [InlineData("http://[::1]:8080/x")]
    [InlineData(@"https://ns.backslash.example\s3cr3t")]
    public void A_rewritten_source_reads_as_the_import_now_stores_it(string url)
    {
        fixture.Sources.Select(s => s.Url).Should().Contain(NightscoutAddress.Shown(url));
    }

    [Fact]
    public void A_mongo_source_and_its_run_are_left_as_they_were()
    {
        fixture.Sources.Single(s => s.Id == ShowNightscoutSourcesByHostFixture.Mongo).Url
            .Should().Be(ShowNightscoutSourcesByHostFixture.MongoUrl);
        fixture.Runs.Single(r => r.SourceId == ShowNightscoutSourcesByHostFixture.Mongo).Description
            .Should().Be(ShowNightscoutSourcesByHostFixture.MongoDescription);
    }

    [Fact]
    public void Every_source_keeps_its_identifier()
    {
        fixture.Sources.Select(s => (s.Id, s.Identifier))
            .Should().BeEquivalentTo(fixture.SourcesBefore.Select(s => (s.Id, s.Identifier)));
    }

    [Fact]
    public void A_second_run_of_the_rewrite_changes_nothing()
    {
        fixture.SourcesAfterSecondRun.Should().BeEquivalentTo(fixture.Sources);
        fixture.RunsAfterSecondRun.Should().BeEquivalentTo(fixture.Runs);
    }
}
