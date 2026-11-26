using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// Runs <c>MaskNightscoutSourceUrls</c> on a real PostgreSQL as the migrator over migration sources
/// in the current form and in the legacy raw form (token, user info, default ports, upper-case host,
/// no scheme, a non-ASCII segment, segments either side of the mask's length), three of one tenant
/// sharing a base, one tenant beside it with the same base, a tenant whose sites are only legacy
/// rows ranked by their last migration and then their creation, and a MongoDB source. Then runs the
/// rewrite a second time, and imports the same site again through the service.
/// </summary>
public class MaskNightscoutSourceUrlsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261124082620_RekeyConnectorResumeState";
    private const string ThisMigration = "20261124161251_MaskNightscoutSourceUrls";

    internal const string Secret = "AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    internal const string CurrentUrl = $"https://ns.example.com/{Secret}";
    internal const string FirstLegacyUrl = $"https://NS.example.com/{Secret}/?token=first";
    internal const string SecondLegacyUrl = $"https://user:pw@ns.example.com:443/{Secret}?token=second#x";
    internal const string SchemelessLegacyUrl = "ns.other.example:8443/nightscout/LongPathSegmentHere00?token=third";
    internal const string ReimportUrl = $"https://ns.example.com/{Secret}/?token=again";
    internal const string FifteenUrl = "https://edge.example/0123456789abcde?token=fifteen";
    internal const string SixteenUrl = "https://edge.example/0123456789abcdef/?token=sixteen";
    internal const string DefaultHttpPortUrl = "http://port.example:80/nightscout/?token=eighty";
    internal const string NonAsciiUrl = "https://unicode.example/sécret/nightscout?token=accent";
    internal const string RankedUrl = "https://ranked.example/nightscout";
    internal const string TiedUrl = "https://tied.example/nightscout";
    internal const string RankedNewerId = "cccccccc-cccc-7ccc-8ccc-cccccccc4099";
    internal const string RankedOlderId = "cbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbb4099";
    internal const string TiedNewerId = "ceeeeeee-eeee-7eee-8eee-eeeeeeee4099";
    internal const string TiedOlderId = "cddddddd-dddd-7ddd-8ddd-dddddddd4099";

    internal static readonly Guid TenantA = Guid.Parse("b1111111-1111-7111-8111-111111114099");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222224099");
    internal static readonly Guid TenantC = Guid.Parse("b3333333-3333-7333-8333-333333334099");

    internal static readonly Guid Current = Guid.Parse("c1111111-1111-7111-8111-111111114099");
    internal static readonly Guid FirstLegacy = Guid.Parse("c2222222-2222-7222-8222-222222224099");
    internal static readonly Guid SecondLegacy = Guid.Parse("c3333333-3333-7333-8333-333333334099");
    internal static readonly Guid Schemeless = Guid.Parse("c4444444-4444-7444-8444-444444444099");
    internal static readonly Guid Mongo = Guid.Parse("c5555555-5555-7555-8555-555555554099");
    internal static readonly Guid OtherTenant = Guid.Parse("c6666666-6666-7666-8666-666666664099");
    internal static readonly Guid Fifteen = Guid.Parse("c7777777-7777-7777-8777-777777774099");
    internal static readonly Guid Sixteen = Guid.Parse("c8888888-8888-7888-8888-888888884099");
    internal static readonly Guid DefaultHttpPort = Guid.Parse("c9999999-9999-7999-8999-999999994099");
    internal static readonly Guid NonAscii = Guid.Parse("caaaaaaa-aaaa-7aaa-8aaa-aaaaaaaa4099");

    private string _migrator = string.Empty;
    private NocturneDbContext? _reader;

    internal IReadOnlyList<SourceRow> Sources { get; private set; } = [];

    internal IReadOnlyList<RunRow> Runs { get; private set; } = [];

    internal IReadOnlyList<SourceRow> SourcesAfterSecondRun { get; private set; } = [];

    internal IReadOnlyList<RunRow> RunsAfterSecondRun { get; private set; } = [];

    internal Guid? ReimportSource { get; private set; }

    internal string? ReimportDescription { get; private set; }

    internal int ApiSourcesOfTenantAAfterReimport { get; private set; }

    internal sealed record SourceRow(
        Guid Id, Guid TenantId, string Mode, string Identifier, string? Url, string? SecretHash,
        DateTime CreatedAt, DateTime? LastMigrationAt, DateTime? LastMigratedDataTimestamp);

    internal sealed record RunRow(Guid Id, Guid SourceId, Guid TenantId, string? Description);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "mask_nightscout_sources");
        _migrator = database.MigratorConnectionString;

        foreach (var tenant in new[] { TenantA, TenantB, TenantC })
            await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES ('{tenant}', 'mask-{tenant:N}', 'mask', true, now(), now());
                """);

        await InsertSourceAsync(Current, TenantA, "Api", CurrentUrl, CurrentUrl,
            createdAt: "2026-01-01", lastMigrationAt: "2026-02-01", lastData: "2026-01-31");
        await InsertSourceAsync(FirstLegacy, TenantA, "Api", FirstLegacyUrl, FirstLegacyUrl,
            createdAt: "2025-06-01", lastMigrationAt: "2026-03-01", lastData: "2026-02-28", secretHash: "first-hash");
        await InsertSourceAsync(SecondLegacy, TenantA, "Api", SecondLegacyUrl, SecondLegacyUrl, createdAt: "2025-01-01");
        await InsertSourceAsync(Schemeless, TenantA, "Api", SchemelessLegacyUrl, SchemelessLegacyUrl, createdAt: "2025-01-01");
        await InsertSourceAsync(Mongo, TenantA, "MongoDb", "mongo-digest", null, createdAt: "2025-01-01");
        await InsertSourceAsync(OtherTenant, TenantB, "Api", CurrentUrl, CurrentUrl, createdAt: "2026-01-01");
        foreach (var (id, url) in new[]
                 {
                     (Fifteen, FifteenUrl), (Sixteen, SixteenUrl), (DefaultHttpPort, DefaultHttpPortUrl), (NonAscii, NonAsciiUrl),
                 })
            await InsertSourceAsync(id, TenantC, "Api", url.TrimEnd('/'), url, createdAt: "2025-01-01");
        await InsertSourceAsync(Guid.Parse(RankedOlderId), TenantC, "Api", $"{RankedUrl}/?token=older", $"{RankedUrl}/?token=older",
            createdAt: "2025-06-01", lastMigrationAt: "2026-01-01");
        await InsertSourceAsync(Guid.Parse(RankedNewerId), TenantC, "Api", $"{RankedUrl}?token=newer", $"{RankedUrl}?token=newer",
            createdAt: "2025-01-01", lastMigrationAt: "2026-02-01");
        await InsertSourceAsync(Guid.Parse(TiedOlderId), TenantC, "Api", $"{TiedUrl}/?token=older", $"{TiedUrl}/?token=older",
            createdAt: "2025-01-01", lastMigrationAt: "2026-01-01");
        await InsertSourceAsync(Guid.Parse(TiedNewerId), TenantC, "Api", $"{TiedUrl}?token=newer", $"{TiedUrl}?token=newer",
            createdAt: "2025-06-01", lastMigrationAt: "2026-01-01");

        await InsertRunAsync(Current, TenantA, "Api", CurrentUrl);
        await InsertRunAsync(FirstLegacy, TenantA, "Api", FirstLegacyUrl);
        await InsertRunAsync(SecondLegacy, TenantA, "Api", SecondLegacyUrl);
        await InsertRunAsync(Schemeless, TenantA, "Api", SchemelessLegacyUrl);
        await InsertRunAsync(Mongo, TenantA, "MongoDb", "MongoDB: nightscout");
        await InsertRunAsync(OtherTenant, TenantB, "Api", CurrentUrl);
        foreach (var id in new[] { Fifteen, Sixteen, DefaultHttpPort, NonAscii })
            await InsertRunAsync(id, TenantC, "Api", "recorded before the rewrite");
        foreach (var id in new[] { RankedOlderId, RankedNewerId, TiedOlderId, TiedNewerId })
            await InsertRunAsync(Guid.Parse(id), TenantC, "Api", "recorded before the rewrite");

        await using (var context = new NocturneDbContext(Options(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync(ThisMigration);

        Sources = await ReadSourcesAsync();
        Runs = await ReadRunsAsync();
        await NpgsqlTestCommands.ExecuteAsync(_migrator, MaskNightscoutSourceUrls.RewriteSql);
        SourcesAfterSecondRun = await ReadSourcesAsync();
        RunsAfterSecondRun = await ReadRunsAsync();

        await using (var context = new NocturneDbContext(Options(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync();

        _reader = new NocturneDbContext(Options(database.AppConnectionString)) { TenantId = TenantA };
        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().Build(),
            _reader.AsFactory(),
            Mock.Of<ISecretEncryptionService>(s => s.Encrypt(It.IsAny<string>()) == "sealed"),
            TimeProvider.System);
        var job = await service.StartMigrationAsync(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = ReimportUrl },
            new TenantContext(TenantA, "mask", "mask", true, IsDemo: false),
            starter: null);

        var runs = await ReadRunsAsync();
        var reimport = runs.Single(r => r.Id == job.Id);
        ReimportSource = reimport.SourceId;
        ReimportDescription = reimport.Description;
        ApiSourcesOfTenantAAfterReimport = (await ReadSourcesAsync()).Count(s => s.TenantId == TenantA && s.Mode == "Api");
    }

    public async Task DisposeAsync()
    {
        if (_reader is not null)
            await _reader.DisposeAsync();
    }

    private static DbContextOptions<NocturneDbContext> Options(string connectionString) =>
        TestContexts.MigrationsOptions(connectionString);

    private Task InsertSourceAsync(
        Guid id, Guid tenant, string mode, string identifier, string? url, string createdAt,
        string? lastMigrationAt = null, string? lastData = null, string? secretHash = null) =>
        NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            INSERT INTO migration_sources (id, tenant_id, mode, source_identifier, nightscout_url, nightscout_api_secret_hash,
                created_at, last_migration_at, last_migrated_data_timestamp)
            VALUES ('{id}', '{tenant}', '{mode}', {Literal(identifier)}, {Literal(url)}, {Literal(secretHash)},
                '{createdAt}', {Literal(lastMigrationAt)}, {Literal(lastData)});
            """);

    private Task InsertRunAsync(Guid source, Guid tenant, string mode, string description) =>
        NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            INSERT INTO migration_runs (id, source_id, tenant_id, mode, state, started_at, created_at,
                entries_migrated, treatments_migrated, revision, source_description)
            VALUES ('{Guid.CreateVersion7()}', '{source}', '{tenant}', '{mode}', 'Completed', now(), now(), 0, 0, 0,
                {Literal(description)});
            """);

    private static string Literal(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''")}'";

    private async Task<IReadOnlyList<SourceRow>> ReadSourcesAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        return await conn.QueryAsync("""
            SELECT id, tenant_id, mode, source_identifier, nightscout_url, nightscout_api_secret_hash,
                   created_at, last_migration_at, last_migrated_data_timestamp
            FROM migration_sources
            """,
            reader => new SourceRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8)));
    }

    private async Task<IReadOnlyList<RunRow>> ReadRunsAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        return await conn.QueryAsync(
            "SELECT id, source_id, tenant_id, source_description FROM migration_runs",
            reader => new RunRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
    }
}

[Trait("Category", "Integration")]
public class MaskNightscoutSourceUrlsTests(MaskNightscoutSourceUrlsFixture fixture)
    : IClassFixture<MaskNightscoutSourceUrlsFixture>
{
    private const string MaskedUrl = "ns.example.com/AGYL…";

    private MaskNightscoutSourceUrlsFixture.SourceRow Source(Guid id) => fixture.Sources.Single(s => s.Id == id);

    [Theory]
    [InlineData(MaskNightscoutSourceUrlsFixture.CurrentUrl)]
    [InlineData(MaskNightscoutSourceUrlsFixture.FirstLegacyUrl)]
    [InlineData(MaskNightscoutSourceUrlsFixture.SecondLegacyUrl)]
    public void Every_form_of_one_site_collapses_onto_the_current_row_with_the_digest_the_import_computes(string url)
    {
        var winner = Source(MaskNightscoutSourceUrlsFixture.Current);

        winner.Identifier.Should().Be(MigrationJob.ApiSourceIdentifier(url));
        winner.Url.Should().Be(NightscoutAddress.Masked(url)).And.Be(MaskedUrl);
    }

    [Fact]
    public void The_rows_that_lost_are_deleted_and_their_watermarks_folded_into_the_winner()
    {
        fixture.Sources.Select(s => s.Id).Should().NotContain(
            [MaskNightscoutSourceUrlsFixture.FirstLegacy, MaskNightscoutSourceUrlsFixture.SecondLegacy]);

        var winner = Source(MaskNightscoutSourceUrlsFixture.Current);
        winner.CreatedAt.Should().Be(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        winner.LastMigrationAt.Should().Be(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        winner.LastMigratedDataTimestamp.Should().Be(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc));
        winner.SecretHash.Should().Be("first-hash");
    }

    [Fact]
    public void The_runs_of_the_rows_that_lost_move_to_the_winner_and_show_the_masked_url()
    {
        var runsOfSite = fixture.Runs.Where(r =>
            r.TenantId == MaskNightscoutSourceUrlsFixture.TenantA
            && r.SourceId != MaskNightscoutSourceUrlsFixture.Schemeless
            && r.SourceId != MaskNightscoutSourceUrlsFixture.Mongo).ToList();

        runsOfSite.Should().HaveCount(3).And.OnlyContain(r =>
            r.SourceId == MaskNightscoutSourceUrlsFixture.Current && r.Description == MaskedUrl);
    }

    [Fact]
    public void A_scheme_less_legacy_url_reads_as_https_and_keeps_its_port()
    {
        var source = Source(MaskNightscoutSourceUrlsFixture.Schemeless);

        source.Identifier.Should().Be(MigrationJob.ApiSourceIdentifier(MaskNightscoutSourceUrlsFixture.SchemelessLegacyUrl));
        source.Url.Should().Be("ns.other.example:8443/nightscout/Long…")
            .And.Be(NightscoutAddress.Masked(MaskNightscoutSourceUrlsFixture.SchemelessLegacyUrl));
        fixture.Runs.Single(r => r.SourceId == MaskNightscoutSourceUrlsFixture.Schemeless).Description.Should().Be(source.Url);
    }

    [Fact]
    public void Another_tenant_with_the_same_site_keeps_its_own_row()
    {
        var other = Source(MaskNightscoutSourceUrlsFixture.OtherTenant);

        other.TenantId.Should().Be(MaskNightscoutSourceUrlsFixture.TenantB);
        other.Identifier.Should().Be(MigrationJob.ApiSourceIdentifier(MaskNightscoutSourceUrlsFixture.CurrentUrl));
        other.Url.Should().Be(MaskedUrl);
        fixture.Runs.Single(r => r.TenantId == MaskNightscoutSourceUrlsFixture.TenantB).Description.Should().Be(MaskedUrl);
    }

    [Fact]
    public void A_mongo_source_and_its_run_are_left_as_they_were()
    {
        var mongo = Source(MaskNightscoutSourceUrlsFixture.Mongo);

        mongo.Identifier.Should().Be("mongo-digest");
        mongo.Url.Should().BeNull();
        fixture.Runs.Single(r => r.SourceId == MaskNightscoutSourceUrlsFixture.Mongo).Description.Should().Be("MongoDB: nightscout");
    }

    [Fact]
    public void Nothing_stored_carries_the_path_secret_or_a_token()
    {
        var stored = fixture.Sources.SelectMany(s => new[] { s.Identifier, s.Url })
            .Concat(fixture.Runs.Select(r => r.Description))
            .OfType<string>()
            .ToList();

        stored.Should().NotContain(v => v.Contains(MaskNightscoutSourceUrlsFixture.Secret) || v.Contains("token"));
    }

    [Theory]
    [InlineData(MaskNightscoutSourceUrlsFixture.FifteenUrl, "edge.example/0123456789abcde")]
    [InlineData(MaskNightscoutSourceUrlsFixture.SixteenUrl, "edge.example/0123…")]
    [InlineData(MaskNightscoutSourceUrlsFixture.DefaultHttpPortUrl, "port.example/nightscout")]
    public void A_legacy_url_is_masked_and_digested_as_the_import_does(string url, string masked)
    {
        var source = fixture.Sources.Single(s => s.Identifier == MigrationJob.ApiSourceIdentifier(url));

        source.Url.Should().Be(NightscoutAddress.Masked(url)).And.Be(masked);
        fixture.Runs.Single(r => r.SourceId == source.Id).Description.Should().Be(masked);
    }

    [Fact]
    public void A_segment_holding_a_character_a_url_escapes_is_masked_however_short()
    {
        var source = Source(MaskNightscoutSourceUrlsFixture.NonAscii);

        source.Url.Should().Be("unicode.example/sécr…/nightscout");
        fixture.Runs.Single(r => r.SourceId == source.Id).Description.Should().Be(source.Url);
    }

    [Theory]
    [InlineData(MaskNightscoutSourceUrlsFixture.RankedUrl, MaskNightscoutSourceUrlsFixture.RankedNewerId, MaskNightscoutSourceUrlsFixture.RankedOlderId)]
    [InlineData(MaskNightscoutSourceUrlsFixture.TiedUrl, MaskNightscoutSourceUrlsFixture.TiedNewerId, MaskNightscoutSourceUrlsFixture.TiedOlderId)]
    public void Among_legacy_rows_the_last_migrated_wins_then_the_newest(string site, string winner, string loser)
    {
        fixture.Sources.Where(s => s.Identifier == MigrationJob.ApiSourceIdentifier(site))
            .Should().ContainSingle().Which.Id.Should().Be(Guid.Parse(winner));
        fixture.Sources.Should().NotContain(s => s.Id == Guid.Parse(loser));
        fixture.Runs.Where(r => r.SourceId == Guid.Parse(winner)).Should().HaveCount(2);
    }

    [Fact]
    public void A_second_run_of_the_rewrite_changes_nothing()
    {
        fixture.SourcesAfterSecondRun.Should().BeEquivalentTo(fixture.Sources);
        fixture.RunsAfterSecondRun.Should().BeEquivalentTo(fixture.Runs);
    }

    [Fact]
    public void A_later_import_of_the_same_site_attaches_to_the_rewritten_row()
    {
        fixture.ReimportSource.Should().Be(MaskNightscoutSourceUrlsFixture.Current);
        fixture.ReimportDescription.Should().Be(NightscoutAddress.Shown(MaskNightscoutSourceUrlsFixture.ReimportUrl));
        fixture.ApiSourcesOfTenantAAfterReimport.Should().Be(2, "the site and the scheme-less one, and no new row");
    }
}
