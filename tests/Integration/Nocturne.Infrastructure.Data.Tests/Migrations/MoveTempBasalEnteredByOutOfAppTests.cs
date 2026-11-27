using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>MoveTempBasalEnteredByOutOfApp</c> on a real PostgreSQL as the migrator, which row level
/// security binds, over two tenants holding decomposer rows that stored <c>enteredBy</c> as their
/// <c>app</c>, rows it must leave (among them rows the code since wrote), and a tenant with more than
/// a page of rows to move; then runs its SQL again, and takes it down.
/// </summary>
public class MoveTempBasalEnteredByOutOfAppFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261127173536_AddPeopleViews";
    private const string Migration = "20261127195950_MoveTempBasalEnteredByOutOfApp";

    internal static readonly Guid TenantA = Guid.Parse("a4394444-4444-7444-8444-444444444444");
    internal static readonly Guid TenantB = Guid.Parse("b4394444-4444-7444-8444-444444444444");
    internal static readonly Guid Bulk = Guid.Parse("c4394444-4444-7444-8444-444444444444");

    internal static readonly Guid Aaps = Row(1);
    internal static readonly Guid AapsWithKeptFields = Row(2);
    internal static readonly Guid Trio = Row(3);
    internal static readonly Guid UserDeleted = Row(4);
    internal static readonly Guid DeviceOnly = Row(5);
    internal static readonly Guid AppAndDeviceDiffer = Row(6);
    internal static readonly Guid V4Client = Row(7);
    internal static readonly Guid Connector = Row(8);
    internal static readonly Guid XDripManual = Row(9);
    internal static readonly Guid InTenantB = Row(10);
    internal static readonly Guid KeepsEnteredBy = Row(11);
    internal static readonly Guid EditedThroughV4 = Row(12);

    internal static readonly Guid[] Moved = [Aaps, AapsWithKeptFields, Trio, UserDeleted, XDripManual, InTenantB];
    internal static readonly Guid[] Left = [DeviceOnly, AppAndDeviceDiffer, V4Client, Connector, KeepsEnteredBy, EditedThroughV4];
    internal static readonly Guid[] HeldNoApp = [DeviceOnly, Connector];

    internal const int BulkRows = MoveTempBasalEnteredByOutOfApp.PageSize + 5;

    private string _migrator = string.Empty;

    internal Dictionary<Guid, StoredRow> Seeded { get; private set; } = [];
    internal Dictionary<Guid, StoredRow> AfterUp { get; private set; } = [];
    internal Dictionary<Guid, StoredRow> AfterSecondRun { get; private set; } = [];
    internal Dictionary<Guid, StoredRow> AfterDown { get; private set; } = [];
    internal int BulkLeftInApp { get; private set; }
    internal int BulkMoved { get; private set; }

    internal sealed record StoredRow(
        string? App,
        string? Device,
        string? LegacyId,
        string? DataSource,
        string Origin,
        string? Evidence,
        string? AdditionalProperties,
        DateTime SysUpdatedAt)
    {
        public JsonElement? Kept(string key) =>
            AdditionalProperties is null
                ? null
                : JsonDocument.Parse(AdditionalProperties).RootElement.TryGetProperty(key, out var value) ? value.Clone() : null;

        public TempBasal ToTempBasal() => new()
        {
            App = App,
            Device = Device,
            LegacyId = LegacyId,
            DataSource = DataSource,
            Origin = Enum.Parse<TempBasalOrigin>(Origin),
            OriginEvidence = Evidence is null ? null : Enum.Parse<TempBasalOriginEvidence>(Evidence),
            AdditionalProperties = AdditionalProperties is null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(AdditionalProperties),
        };
    }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "move_temp_basal_entered_by");
        _migrator = database.MigratorConnectionString;

        foreach (var tenant in new[] { TenantA, TenantB, Bulk })
            await NpgsqlTestCommands.SeedTenantAsync(_migrator, tenant, $"tb-{tenant:N}", "tb");

        await InsertAsync(TenantA, Aaps, "openaps://AndroidAPS", "openaps://AndroidAPS", "aaps-1", origin: "Manual");
        await InsertAsync(TenantA, AapsWithKeptFields, "openaps://AndroidAPS", "openaps://AndroidAPS", "aaps-2",
            kept: """{"isValid":true,"duration":30}""", evidence: "Uploader");
        await InsertAsync(TenantA, Trio, "Trio", "Trio", "trio-1");
        await InsertAsync(TenantA, UserDeleted, "Trio", "Trio", "trio-2", deleted: true);
        await InsertAsync(TenantA, DeviceOnly, app: null, device: "Pixel", "pixel-1");
        await InsertAsync(TenantA, AppAndDeviceDiffer, "AAPS", "Pixel", "aaps-3");
        await InsertAsync(TenantA, V4Client, "Trio", "Trio", legacyId: null);
        await InsertAsync(TenantA, Connector, app: null, device: "MiniMed 780G", "carelink-1", dataSource: "carelink-connector");
        await InsertAsync(TenantA, XDripManual, "xDrip", "xDrip", "xdrip-1", origin: "Manual");
        await InsertAsync(TenantB, InTenantB, "openaps://AndroidAPS", "openaps://AndroidAPS", "aaps-b");
        await InsertAsync(TenantA, KeepsEnteredBy, "Trio", "Trio", "trio-3", kept: """{"enteredBy":"Trio"}""");
        await InsertAsync(TenantA, EditedThroughV4, "Trio", "Pixel", "trio-4");

        await NpgsqlTestCommands.ExecuteAsTenantAsync(_migrator, Bulk, $"""
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, origin, legacy_id, app, device)
            SELECT gen_random_uuid(), '{Bulk}', now(), 'Algorithm', 'bulk-' || n, 'Trio', 'Trio'
            FROM generate_series(1, {BulkRows}) AS n;
            """);

        Seeded = await ReadAsync();
        await MigrateToAsync(Migration);
        AfterUp = await ReadAsync();
        BulkLeftInApp = await BulkCountAsync("app IS NOT NULL");
        BulkMoved = await BulkCountAsync("additional_properties ->> 'enteredBy' = 'Trio'");

        await NpgsqlTestCommands.ExecuteAsync(_migrator, MoveTempBasalEnteredByOutOfApp.UpSql);
        AfterSecondRun = await ReadAsync();

        await MigrateToAsync(PriorMigration);
        AfterDown = await ReadAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task InsertAsync(
        Guid tenant, Guid id, string? app, string? device, string? legacyId,
        string origin = "Algorithm", string? kept = null, string? evidence = null, string? dataSource = null,
        bool deleted = false) =>
        NpgsqlTestCommands.ExecuteAsTenantAsync(_migrator, tenant, $"""
            INSERT INTO temp_basals
                (id, tenant_id, start_timestamp, origin, origin_evidence, legacy_id, app, device, data_source,
                 additional_properties, deleted_at, deleted_by_user)
            VALUES ('{id}', '{tenant}', now(), '{origin}', {Text(evidence)}, {Text(legacyId)}, {Text(app)},
                    {Text(device)}, {Text(dataSource)}, {(kept is null ? "NULL" : $"'{kept}'::jsonb")},
                    {(deleted ? "now()" : "NULL")}, {deleted});
            """);

    private async Task<Dictionary<Guid, StoredRow>> ReadAsync()
    {
        var rows = new Dictionary<Guid, StoredRow>();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using var conn = await NpgsqlTestCommands.OpenAsync(_migrator);
            await conn.PinTenantAsync(tenant);
            var read = await conn.QueryAsync(
                """
                SELECT id, app, device, legacy_id, data_source, origin, origin_evidence,
                       additional_properties::text, sys_updated_at
                FROM temp_basals
                """,
                r => (r.GetGuid(0), new StoredRow(
                    Nullable(r, 1), Nullable(r, 2), Nullable(r, 3), Nullable(r, 4), r.GetString(5),
                    Nullable(r, 6), Nullable(r, 7), r.GetDateTime(8))));
            foreach (var (id, row) in read)
                rows[id] = row;
        }
        return rows;
    }

    private async Task<int> BulkCountAsync(string condition)
    {
        await using var conn = await NpgsqlTestCommands.OpenAsync(_migrator);
        await conn.PinTenantAsync(Bulk);
        return (int)await conn.ScalarAsync<long>($"SELECT count(*) FROM temp_basals WHERE {condition}");
    }

    private static string? Nullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string Text(string? value) => value is null ? "NULL" : $"'{value}'";

    private static Guid Row(int n) => Guid.Parse($"e0000000-0000-7000-8000-{n:D12}");

    private async Task MigrateToAsync(string target)
    {
        await using var context = new NocturneDbContext(TestContexts.MigrationsOptions(_migrator));
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class MoveTempBasalEnteredByOutOfAppTests(MoveTempBasalEnteredByOutOfAppFixture fixture)
    : IClassFixture<MoveTempBasalEnteredByOutOfAppFixture>
{
    [Fact]
    public void Up_moves_each_decomposer_rows_enteredBy_out_of_app()
    {
        foreach (var id in MoveTempBasalEnteredByOutOfAppFixture.Moved)
        {
            var seeded = fixture.Seeded[id];
            var moved = fixture.AfterUp[id];
            moved.App.Should().BeNull();
            moved.Device.Should().Be(seeded.Device);
            moved.Kept("enteredBy")?.GetString().Should().Be(seeded.App);
        }
    }

    [Fact]
    public void Up_keeps_the_other_kept_fields() =>
        JsonDocument.Parse(fixture.AfterUp[MoveTempBasalEnteredByOutOfAppFixture.AapsWithKeptFields].AdditionalProperties!)
            .RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetRawText()))
            .Should().BeEquivalentTo(new[] { ("isValid", "true"), ("duration", "30"), ("enteredBy", "\"openaps://AndroidAPS\"") });

    [Fact]
    public void Up_leaves_rows_whose_app_is_no_copy_of_enteredBy()
    {
        foreach (var id in MoveTempBasalEnteredByOutOfAppFixture.Left)
            fixture.AfterUp[id].Should().Be(fixture.Seeded[id]);
    }

    [Fact]
    public void Up_leaves_the_served_stamp_alone()
    {
        foreach (var (id, seeded) in fixture.Seeded)
            fixture.AfterUp[id].SysUpdatedAt.Should().Be(seeded.SysUpdatedAt);
    }

    [Fact]
    public void Up_moves_every_page_of_a_tenant()
    {
        fixture.BulkLeftInApp.Should().Be(0);
        fixture.BulkMoved.Should().Be(MoveTempBasalEnteredByOutOfAppFixture.BulkRows);
    }

    [Fact]
    public void Up_leaves_a_row_that_already_keeps_its_enteredBy() =>
        fixture.AfterUp[MoveTempBasalEnteredByOutOfAppFixture.KeepsEnteredBy].App.Should().Be("Trio");

    [Fact]
    public void Every_rows_origin_evidence_reads_as_it_did_when_app_held_enteredBy()
    {
        foreach (var (id, seeded) in fixture.Seeded.Where(r => r.Key != MoveTempBasalEnteredByOutOfAppFixture.EditedThroughV4))
            TempBasalOriginProvenance.EvidenceOf(fixture.AfterUp[id].ToTempBasal())
                .Should().Be(EvidenceWhenAppHeldEnteredBy(seeded), id.ToString());
    }

    [Fact]
    public void A_decomposer_row_edited_through_V4_now_names_its_uploader_by_its_device_and_serves_no_enteredBy()
    {
        var seeded = fixture.Seeded[MoveTempBasalEnteredByOutOfAppFixture.EditedThroughV4];
        var row = fixture.AfterUp[MoveTempBasalEnteredByOutOfAppFixture.EditedThroughV4].ToTempBasal();

        EvidenceWhenAppHeldEnteredBy(seeded).Should().Be(TempBasalOriginEvidence.Uploader);
        TempBasalOriginProvenance.EvidenceOf(row).Should().Be(TempBasalOriginEvidence.Reported);
        var served = TempBasalToTreatmentMapper.ToTreatment(row);
        served.EnteredBy.Should().BeNull();
        served.AdditionalProperties!["app"].Should().Be("Trio");
    }

    [Fact]
    public void Running_it_again_changes_nothing() =>
        fixture.AfterSecondRun.Should().BeEquivalentTo(fixture.AfterUp);

    [Fact]
    public void Down_puts_each_moved_enteredBy_back_in_app()
    {
        foreach (var id in MoveTempBasalEnteredByOutOfAppFixture.Moved
                     .Concat(MoveTempBasalEnteredByOutOfAppFixture.HeldNoApp)
                     .Append(MoveTempBasalEnteredByOutOfAppFixture.V4Client))
            fixture.AfterDown[id].Should().Be(fixture.Seeded[id], id.ToString());
    }

    [Fact]
    public void Down_gives_each_decomposer_row_the_app_the_old_code_wrote()
    {
        var keptEnteredBy = fixture.AfterDown[MoveTempBasalEnteredByOutOfAppFixture.KeepsEnteredBy];
        keptEnteredBy.App.Should().Be("Trio");
        keptEnteredBy.AdditionalProperties.Should().BeNull();

        foreach (var id in new[] { MoveTempBasalEnteredByOutOfAppFixture.AppAndDeviceDiffer, MoveTempBasalEnteredByOutOfAppFixture.EditedThroughV4 })
            fixture.AfterDown[id].Should().Be(fixture.Seeded[id] with { App = null }, id.ToString());
    }

    private static TempBasalOriginEvidence EvidenceWhenAppHeldEnteredBy(MoveTempBasalEnteredByOutOfAppFixture.StoredRow seeded) =>
        TempBasalOriginProvenance.EvidenceOf(
            Enum.Parse<TempBasalOrigin>(seeded.Origin),
            seeded.Evidence is null ? null : Enum.Parse<TempBasalOriginEvidence>(seeded.Evidence),
            automaticFlag: null,
            seeded.App,
            seeded.DataSource,
            seeded.LegacyId);
}
