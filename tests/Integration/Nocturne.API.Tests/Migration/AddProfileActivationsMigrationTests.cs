using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// Runs <c>AddProfileActivations</c> over Profile spans of every shape a shipped instance holds, in two
/// tenants beside a span of another category, then down and up again, on a real PostgreSQL; and holds
/// what the SQL wrote to what <see cref="ProfileActivationLegacyFields"/> writes for the same metadata.
/// </summary>
public sealed class AddProfileActivationsMigrationFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261020015113_ConvertMotionClassActivitiesToMotionSpans";

    /// <summary>
    /// Migrated to rather than the latest: <c>RemoveLegacyProfileSpansAndSwitchSnapshots</c> deletes the
    /// spans a second up would convert again.
    /// </summary>
    private const string MigrationUnderTest = "20261021035454_AddProfileActivations";

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly DateTime Day = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime CreatedAt = new(2026, 6, 2, 1, 2, 3, DateTimeKind.Utc);
    internal static readonly DateTime UpdatedAt = new(2026, 6, 3, 4, 5, 6, DateTimeKind.Utc);

    internal static readonly Guid AapsSwitch = Guid.Parse("d0000000-0000-7000-8000-000000000001");
    internal static readonly Guid AapsEffective = Guid.Parse("d0000000-0000-7000-8000-000000000002");
    internal static readonly Guid CareportalSuperseded = Guid.Parse("d0000000-0000-7000-8000-000000000003");
    internal static readonly Guid OldTemporary = Guid.Parse("d0000000-0000-7000-8000-000000000004");
    internal static readonly Guid DeletedByUser = Guid.Parse("d0000000-0000-7000-8000-000000000005");
    internal static readonly Guid Glooko = Guid.Parse("d0000000-0000-7000-8000-000000000006");
    internal static readonly Guid Native = Guid.Parse("d0000000-0000-7000-8000-000000000007");
    internal static readonly Guid DuplicateOlder = Guid.Parse("d0000000-0000-7000-8000-000000000008");
    internal static readonly Guid DuplicateNewer = Guid.Parse("d0000000-0000-7000-8000-000000000009");
    internal static readonly Guid Malformed = Guid.Parse("d0000000-0000-7000-8000-00000000000a");
    internal static readonly Guid Override = Guid.Parse("d0000000-0000-7000-8000-00000000000b");
    internal static readonly Guid InB = Guid.Parse("d0000000-0000-7000-8000-00000000000c");
    internal static readonly Guid OutOfRange = Guid.Parse("d0000000-0000-7000-8000-00000000000d");
    internal static readonly Guid PairOlder = Guid.Parse("d0000000-0000-7000-8000-00000000000e");
    internal static readonly Guid PairNewer = Guid.Parse("d0000000-0000-7000-8000-00000000000f");
    internal static readonly Guid AapsV3Effective = Guid.Parse("d0000000-0000-7000-8000-000000000010");

    /// <summary>A profile whose numbers fit no column: each converts as absent.</summary>
    internal const string OutOfRangeProfileJson =
        """{"units":"mg/dl","dia":1e400,"carbs_hr":1e12,"basal":[{"time":"01:00","timeAsSeconds":1e10,"value":"1e99999"}],"carbratio":[{"time":"00:00","value":10}],"sens":[{"time":"00:00","value":40}],"target_low":[{"time":"00:00","value":100}],"target_high":[{"time":"00:00","value":120}]}""";

    internal const string BaseProfileJson =
        """{"units":"mg/dl","dia":4,"carbs_hr":25,"timezone":"Australia/Sydney","basal":[{"time":"00:00","value":0.8},{"time":"06:30","value":1.0}],"carbratio":[{"time":"00:00","value":10}],"sens":[{"time":"00:00","value":40}],"target_low":[{"time":"00:00","value":100}],"target_high":[{"time":"00:00","value":120}]}""";

    internal const string EffectiveProfileJson =
        """{"units":"mmol","dia":5,"timezone":"Australia/Brisbane","sens":[{"time":"00:00","timeAsSeconds":0,"value":2.0}],"carbratio":[{"time":"00:00","timeAsSeconds":0,"value":9.1}],"basal":[{"time":"00:00","timeAsSeconds":0,"value":0.88}],"target_low":[{"time":"00:00","timeAsSeconds":0,"value":5.5}],"target_high":[{"time":"00:00","timeAsSeconds":0,"value":6}]}""";

    private string _migrator = string.Empty;

    internal Dictionary<Guid, (string Category, Dictionary<string, object>? Metadata, DateTime Start, DateTime? End, bool Superseded)> Spans { get; } = [];
    internal Dictionary<Guid, (Guid Tenant, ProfileActivationEntity Row, bool DeletedByUser)> AfterUp { get; } = [];
    internal Dictionary<Guid, (Guid Tenant, ProfileActivationEntity Row, bool DeletedByUser)> AfterSecondUp { get; } = [];
    internal bool TableAfterDown { get; private set; }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_profile_activations");
        _migrator = database.MigratorConnectionString;

        await SeedAsync();
        await MigrateToAsync(MigrationUnderTest);
        await ReadAsync(AfterUp);
        await MigrateToAsync(PriorMigration);
        TableAfterDown = await TableExistsAsync();
        await MigrateToAsync(MigrationUnderTest);
        await ReadAsync(AfterSecondUp);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<bool> TableExistsAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass('public.profile_activations') IS NOT NULL;";
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private void Span(
        Guid id, string? originalId, string source, string metadata, DateTime start, DateTime? end = null,
        bool superseded = false, string category = "Profile")
    {
        Spans[id] = (category, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadata)!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value), start, end, superseded);
        _seed.Add((id, originalId, source, metadata, start, end, superseded, category));
    }

    private readonly List<(Guid Id, string? OriginalId, string Source, string Metadata, DateTime Start, DateTime? End, bool Superseded, string Category)> _seed = [];

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private async Task SeedAsync()
    {
        var aapsStart = Day.AddHours(8);
        Span(AapsSwitch, "ps-1", "openaps://AndroidAPS", Json(new Dictionary<string, object>
        {
            ["profileName"] = "Rhys (110%,1h)", ["originalProfileName"] = "Rhys", ["percentage"] = 110,
            ["timeshift"] = 3_600_000, ["duration"] = 60, ["originalDuration"] = 3_600_000,
            ["profileJson"] = BaseProfileJson, ["utcOffset"] = 600, ["enteredBy"] = "openaps://AndroidAPS",
            ["insulinName"] = "Fiasp", ["insulinDia"] = "5.0", ["insulinPeak"] = "55",
            ["insulinConcentration"] = "100", ["insulinCurve"] = "rapid-acting",
            ["pumpId"] = 4102, ["notes"] = "lunch",
        }), aapsStart, aapsStart.AddSeconds(90), superseded: true);
        Span(AapsEffective, "eps-1", "openaps://AndroidAPS", Json(new Dictionary<string, object>
        {
            ["eventType"] = "Note", ["profileName"] = "Rhys (110%,1h)", ["originalProfileName"] = "Rhys",
            ["originalCustomizedName"] = "Rhys (110%,1h)", ["originalPercentage"] = 110, ["percentage"] = 110,
            ["originalTimeshift"] = 3_600_000, ["timeshift"] = 1, ["originalDuration"] = 3_600_000,
            ["originalEnd"] = new DateTimeOffset(aapsStart.AddHours(1)).ToUnixTimeMilliseconds(),
            ["profileJson"] = EffectiveProfileJson, ["notes"] = "Rhys (110%,1h)", ["utcOffset"] = 600,
            ["enteredBy"] = "openaps://AndroidAPS",
        }), aapsStart.AddSeconds(90));
        Span(CareportalSuperseded, "cp-1", "careportal", Json(new Dictionary<string, object>
        {
            ["profileName"] = "Weekend", ["timeshift"] = 2, ["utcOffset"] = 0, ["enteredBy"] = "careportal",
        }), Day.AddHours(1), Day.AddHours(2), superseded: true);
        Span(OldTemporary, "cp-2", "careportal", Json(new Dictionary<string, object>
        {
            ["profileName"] = "Sick", ["utcOffset"] = 0, ["enteredBy"] = "careportal",
        }), Day.AddHours(3), Day.AddHours(3).AddMinutes(45));
        Span(DeletedByUser, "cp-3", "careportal", """{"profileName":"Old","utcOffset":0}""", Day.AddHours(4));
        Span(Glooko, "glooko_profile_1", "glooko-connector", """{"profileName":"Pump A"}""", Day.AddHours(5), Day.AddHours(6));
        Span(Native, null, "demo-service", """{"profileName":"Demo Profile"}""", Day);
        Span(DuplicateOlder, "dup", "careportal", """{"profileName":"Twice","utcOffset":0}""", Day.AddHours(6));
        Span(DuplicateNewer, "dup", "careportal", """{"profileName":"Twice","utcOffset":0}""", Day.AddHours(6));
        Span(Malformed, "bad", "careportal", Json(new Dictionary<string, object>
        {
            ["profileName"] = "Bad", ["profileJson"] = "{not json", ["utcOffset"] = 0,
        }), Day.AddHours(7));
        Span(Override, "ov-1", "loop", """{"collection":"treatments","utcOffset":0}""", Day.AddHours(9), category: "Override");
        Span(InB, "ps-b", "careportal", """{"profileName":"Other","utcOffset":0}""", Day.AddHours(2));
        Span(OutOfRange, "huge", "careportal", $$"""
            {"profileName":"Huge","percentage":1e400,"timeshift":"1e30","duration":1e20,"utcOffset":"+10:00",
             "insulinDia":5,"insulinPeak":1e12,"profileJson":{{JsonSerializer.Serialize(OutOfRangeProfileJson)}}}
            """, Day.AddHours(10));
        // One legacy id under two sources: the unique index is on the legacy id alone.
        Span(PairOlder, "pair", "careportal", """{"profileName":"Pair","utcOffset":0,"enteredBy":"careportal"}""", Day.AddHours(11));
        Span(PairNewer, "pair", "xdrip", """{"profileName":"Pair","utcOffset":0,"enteredBy":"careportal"}""", Day.AddHours(11));
        Span(AapsV3Effective, "eps-v3", "AAPS", Json(new Dictionary<string, object>
        {
            ["eventType"] = "Note", ["profileName"] = "Rhys (110%,1h)", ["originalProfileName"] = "Rhys",
            ["originalPercentage"] = 110, ["percentage"] = 110, ["originalTimeshift"] = 3_600_000,
            ["timeshift"] = 1, ["originalDuration"] = 3_600_000, ["utcOffset"] = 600, ["app"] = "AAPS",
        }), aapsStart.AddSeconds(30));

        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await Execute(conn, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{TenantA}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{TenantB}', 'tenant-b', 'Tenant B', true, now(), now());
            """);

        // The superseded spans point at the effective one, so it goes first.
        foreach (var span in _seed.OrderBy(s => s.Id == AapsEffective ? 0 : 1))
        {
            var tenant = span.Id == InB ? TenantB : TenantA;
            await Execute(conn, $"SELECT set_config('app.current_tenant_id', '{tenant}', false);");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO state_spans
                  (id, tenant_id, category, state, start_timestamp, end_timestamp, source, original_id, metadata,
                   superseded_by_id, created_at, updated_at, deleted_at, deleted_by_user)
                VALUES (@id, @tenant, @category, 'Active', @start, @end, @source, @originalId, @metadata::jsonb,
                   @supersededBy, @created, @updated, @deletedAt, @deletedByUser);
                """;
            cmd.Parameters.AddWithValue("id", span.Id);
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("category", span.Category);
            cmd.Parameters.AddWithValue("start", span.Start);
            cmd.Parameters.AddWithValue("end", (object?)span.End ?? DBNull.Value);
            cmd.Parameters.AddWithValue("source", span.Source);
            cmd.Parameters.AddWithValue("originalId", (object?)span.OriginalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("metadata", span.Metadata);
            cmd.Parameters.AddWithValue("supersededBy", span.Superseded ? AapsEffective : DBNull.Value);
            cmd.Parameters.AddWithValue("created", CreatedAt);
            cmd.Parameters.AddWithValue("updated",
                span.Id == DuplicateNewer || span.Id == PairNewer ? UpdatedAt.AddMinutes(1) : UpdatedAt);
            cmd.Parameters.AddWithValue("deletedAt", span.Id == DeletedByUser ? UpdatedAt : DBNull.Value);
            cmd.Parameters.AddWithValue("deletedByUser", span.Id == DeletedByUser);
            await cmd.ExecuteNonQueryAsync();
        }

        await Execute(conn, "SELECT set_config('app.current_tenant_id', '', false);");
    }

    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ReadAsync(Dictionary<Guid, (Guid Tenant, ProfileActivationEntity Row, bool DeletedByUser)> rows)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await Execute(conn, $"SELECT set_config('app.current_tenant_id', '{tenant}', false);");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, start_timestamp, end_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms,
                       requested_duration_ms, requested_activation_id, snapshot::text, snapshot_is_customized,
                       entered_by, app, device, data_source, utc_offset, legacy_id, sync_identifier,
                       additional_properties::text, sys_created_at, sys_updated_at, deleted_at, deleted_by_user, tenant_id
                FROM profile_activations;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var row = new ProfileActivationEntity
                {
                    Id = reader.GetGuid(0),
                    Timestamp = reader.GetDateTime(1),
                    EndTimestamp = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    Phase = reader.GetString(3),
                    Origin = reader.GetString(4),
                    BaseProfileName = reader.GetString(5),
                    Percentage = reader.GetInt32(6),
                    TimeshiftMs = reader.GetInt64(7),
                    RequestedDurationMs = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    RequestedActivationId = reader.IsDBNull(9) ? null : reader.GetGuid(9),
                    SnapshotJson = Text(10),
                    SnapshotIsCustomized = reader.GetBoolean(11),
                    EnteredBy = Text(12),
                    App = Text(13),
                    Device = Text(14),
                    DataSource = Text(15),
                    UtcOffset = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                    LegacyId = Text(17),
                    SyncIdentifier = Text(18),
                    AdditionalPropertiesJson = Text(19),
                    SysCreatedAt = reader.GetDateTime(20),
                    SysUpdatedAt = reader.GetDateTime(21),
                    DeletedAt = reader.IsDBNull(22) ? null : reader.GetDateTime(22),
                    TenantId = reader.GetGuid(24),
                };
                rows[row.Id] = (tenant, row, reader.GetBoolean(23));
            }
        }
    }
}

/// <inheritdoc cref="AddProfileActivationsMigrationFixture"/>
[Trait("Category", "Integration")]
public class AddProfileActivationsMigrationTests(AddProfileActivationsMigrationFixture fixture)
    : IClassFixture<AddProfileActivationsMigrationFixture>
{
    private ProfileActivationEntity Row(Guid id) => fixture.AfterUp[id].Row;

    private ProfileActivation Domain(Guid id) => ProfileActivationMapper.ToDomainModel(Row(id));

    [Fact]
    public void Every_profile_span_becomes_an_activation_under_its_own_id_in_its_own_tenant()
    {
        var profileSpans = fixture.Spans.Where(s => s.Value.Category == "Profile").Select(s => s.Key).ToList();

        fixture.AfterUp.Keys.Should().BeEquivalentTo(profileSpans);
        fixture.AfterUp[AddProfileActivationsMigrationFixture.InB].Tenant.Should().Be(AddProfileActivationsMigrationFixture.TenantB);
        fixture.AfterUp[AddProfileActivationsMigrationFixture.InB].Row.TenantId.Should().Be(AddProfileActivationsMigrationFixture.TenantB);
        fixture.AfterUp.Should().NotContainKey(AddProfileActivationsMigrationFixture.Override);
    }

    [Fact]
    public void Stamps_and_keys_are_kept()
    {
        var aaps = Row(AddProfileActivationsMigrationFixture.AapsSwitch);
        aaps.SysCreatedAt.Should().Be(AddProfileActivationsMigrationFixture.CreatedAt);
        aaps.SysUpdatedAt.Should().Be(AddProfileActivationsMigrationFixture.UpdatedAt);
        aaps.LegacyId.Should().Be("ps-1");
        aaps.SyncIdentifier.Should().BeNull();
        aaps.DataSource.Should().BeNull("the span's source was its uploader, not a data source");

        var glooko = Row(AddProfileActivationsMigrationFixture.Glooko);
        glooko.LegacyId.Should().BeNull("a connector's span was never served as a treatment");
        glooko.SyncIdentifier.Should().Be("glooko_profile_1");
        glooko.DataSource.Should().Be("glooko-connector");
        glooko.Phase.Should().Be(nameof(ProfileActivationPhase.Effective));
        glooko.Origin.Should().Be(nameof(ProfileActivationOrigin.Connector));
        glooko.EndTimestamp.Should().Be(AddProfileActivationsMigrationFixture.Day.AddHours(6));

        var native = Row(AddProfileActivationsMigrationFixture.Native);
        native.LegacyId.Should().BeNull();
        native.SyncIdentifier.Should().BeNull();
        native.DataSource.Should().Be("demo-service");
        native.Origin.Should().Be(nameof(ProfileActivationOrigin.User));
    }

    [Fact]
    public void Soft_delete_state_is_kept()
    {
        var deleted = fixture.AfterUp[AddProfileActivationsMigrationFixture.DeletedByUser];
        deleted.Row.DeletedAt.Should().Be(AddProfileActivationsMigrationFixture.UpdatedAt);
        deleted.DeletedByUser.Should().BeTrue();
    }

    [Fact]
    public void A_live_duplicate_of_a_legacy_id_keeps_its_newest_row_and_tombstones_the_rest_for_history()
    {
        var newer = fixture.AfterUp[AddProfileActivationsMigrationFixture.DuplicateNewer];
        var older = fixture.AfterUp[AddProfileActivationsMigrationFixture.DuplicateOlder];

        newer.Row.DeletedAt.Should().BeNull();
        older.Row.DeletedAt.Should().NotBeNull();
        older.DeletedByUser.Should().BeFalse("the cleanup is not the user deleting it");
        older.Row.SysUpdatedAt.Should().BeAfter(AddProfileActivationsMigrationFixture.UpdatedAt, "history has to deliver the delete");
    }

    [Fact]
    public void A_live_duplicate_of_a_legacy_id_under_two_sources_keeps_one_live_row()
    {
        fixture.AfterUp[AddProfileActivationsMigrationFixture.PairNewer].Row.DeletedAt.Should().BeNull();
        fixture.AfterUp[AddProfileActivationsMigrationFixture.PairNewer].Row.DataSource.Should().Be("xdrip");
        fixture.AfterUp[AddProfileActivationsMigrationFixture.PairOlder].Row.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Tombstoned_duplicates_each_get_their_own_history_stamp()
    {
        var older = fixture.AfterUp[AddProfileActivationsMigrationFixture.DuplicateOlder].Row.SysUpdatedAt;
        var pair = fixture.AfterUp[AddProfileActivationsMigrationFixture.PairOlder].Row.SysUpdatedAt;

        older.Should().NotBe(pair, "history pages on sys_updated_at, so two deletes on one stamp can skip one");
    }

    [Fact]
    public void Values_out_of_their_columns_range_convert_as_absent()
    {
        var activation = Domain(AddProfileActivationsMigrationFixture.OutOfRange);

        activation.Percentage.Should().Be(100);
        activation.TimeshiftMs.Should().Be(0);
        activation.RequestedDurationMs.Should().BeNull();
        activation.UtcOffset.Should().BeNull();
        activation.Snapshot!.Dia.Should().Be(3);
        activation.Snapshot.CarbsHr.Should().Be(20);
        activation.Snapshot.Insulin.Should().BeNull("a peak no int holds is no insulin");
        activation.Snapshot.Basal!.Single().Should().BeEquivalentTo(new { TimeAsSeconds = 3600, Value = 0.0 });
    }

    [Fact]
    public void A_switch_uploaded_without_a_percentage_reads_back_without_one()
    {
        var activation = Domain(AddProfileActivationsMigrationFixture.OldTemporary);

        activation.Percentage.Should().Be(100);
        LegacyTreatmentTables.ProjectProfileActivation(activation).Percentage.Should().BeNull();
        LegacyTreatmentTables.ProjectProfileActivation(Domain(AddProfileActivationsMigrationFixture.AapsSwitch))
            .Percentage.Should().Be(110);
    }

    [Fact]
    public void An_effective_switch_sent_by_app_confirms_the_switch_sent_by_uploader()
    {
        Domain(AddProfileActivationsMigrationFixture.AapsV3Effective).RequestedActivationId
            .Should().Be(AddProfileActivationsMigrationFixture.AapsSwitch, "v3 AAPS names itself by app, v1 by enteredBy");
    }

    [Fact]
    public void An_aaps_switch_is_requested_with_its_shift_in_milliseconds_and_its_base_snapshot()
    {
        var activation = Domain(AddProfileActivationsMigrationFixture.AapsSwitch);

        activation.Phase.Should().Be(ProfileActivationPhase.Requested);
        activation.Origin.Should().Be(ProfileActivationOrigin.Uploader);
        activation.BaseProfileName.Should().Be("Rhys");
        activation.Percentage.Should().Be(110);
        activation.TimeshiftMs.Should().Be(3_600_000);
        activation.RequestedDurationMs.Should().Be(3_600_000);
        activation.SnapshotIsCustomized.Should().BeFalse();
        activation.Snapshot!.Basal!.Select(e => (e.TimeAsSeconds, e.Value)).Should().Equal((0, 0.8), (23_400, 1.0));
        activation.Snapshot.Insulin!.InsulinName.Should().Be("Fiasp");
        activation.Snapshot.Insulin.Dia.Should().Be(5);
    }

    [Fact]
    public void An_effective_switch_confirms_the_switch_it_runs_with_its_customized_snapshot()
    {
        var activation = Domain(AddProfileActivationsMigrationFixture.AapsEffective);

        activation.Phase.Should().Be(ProfileActivationPhase.Effective);
        activation.SnapshotIsCustomized.Should().BeTrue();
        activation.RequestedActivationId.Should().Be(AddProfileActivationsMigrationFixture.AapsSwitch);
        activation.Snapshot!.Sensitivity!.Single().Value.Should().Be(36);
        activation.Snapshot.Targets!.Single().Should().BeEquivalentTo(new { Low = 99.0, High = 108.0 });
        activation.EndTimestamp.Should().BeNull("the span had none of its own");
    }

    [Fact]
    public void Old_spans_have_their_shift_unit_and_length_inferred()
    {
        var careportal = Domain(AddProfileActivationsMigrationFixture.CareportalSuperseded);
        careportal.TimeshiftMs.Should().Be(7_200_000);
        careportal.RequestedDurationMs.Should().BeNull("its end is where a later switch took over");
        careportal.AdditionalProperties.Should().ContainKey(ProfileSpanMetadata.TimeshiftUnitKey);

        Domain(AddProfileActivationsMigrationFixture.OldTemporary).RequestedDurationMs.Should().Be(45 * 60_000);
        Domain(AddProfileActivationsMigrationFixture.Malformed).Snapshot.Should().BeNull();
    }

    /// <summary>
    /// The SQL writes what <see cref="ProfileActivationLegacyFields.FromSpanMetadata"/> writes for the
    /// same span: the columns, the residue the treatment is read back from, and the snapshot.
    /// </summary>
    [Fact]
    public void The_sql_conversion_agrees_with_the_code_that_writes_new_activations()
    {
        foreach (var (id, span) in fixture.Spans.Where(s => s.Value.Category == "Profile"
                     && s.Key != AddProfileActivationsMigrationFixture.Glooko
                     && s.Key != AddProfileActivationsMigrationFixture.DuplicateOlder
                     && s.Key != AddProfileActivationsMigrationFixture.PairOlder
                     && s.Key != AddProfileActivationsMigrationFixture.OutOfRange))
        {
            var expected = ProfileActivationLegacyFields.FromSpanMetadata(
                span.Metadata!, span.Start, span.End, span.Superseded);
            var actual = Domain(id);

            actual.Should().BeEquivalentTo(expected, options => options
                .Including(a => a.BaseProfileName)
                .Including(a => a.Percentage)
                .Including(a => a.TimeshiftMs)
                .Including(a => a.RequestedDurationMs)
                .Including(a => a.SnapshotIsCustomized)
                .Including(a => a.EnteredBy)
                .Including(a => a.App)
                .Including(a => a.UtcOffset)
                .Including(a => a.Snapshot),
                because: $"span {id} converts one way");

            Residue(actual).Should().BeEquivalentTo(Residue(expected), $"span {id} keeps one residue");

            if (actual.LegacyId is not null)
            {
                expected.LegacyId = actual.LegacyId;
                expected.Id = actual.Id;
                expected.EndTimestamp = actual.EndTimestamp;
                expected.DataSource = actual.DataSource;
                LegacyTreatmentTables.ProjectProfileActivation(actual).Should().BeEquivalentTo(
                    LegacyTreatmentTables.ProjectProfileActivation(expected),
                    options => options.Excluding(t => t.Duration).Excluding(t => t.DurationInMilliseconds),
                    $"span {id} reads back one way");
            }
        }
    }

    [Fact]
    public void Down_drops_the_table_and_up_again_converts_the_same_rows()
    {
        fixture.TableAfterDown.Should().BeFalse();
        fixture.AfterSecondUp.Keys.Should().BeEquivalentTo(fixture.AfterUp.Keys);
        foreach (var (id, row) in fixture.AfterUp.Where(r => r.Key != AddProfileActivationsMigrationFixture.DuplicateOlder
                     && r.Key != AddProfileActivationsMigrationFixture.PairOlder))
            fixture.AfterSecondUp[id].Row.Should().BeEquivalentTo(row.Row);
    }

    private static Dictionary<string, string> Residue(ProfileActivation activation) =>
        (activation.AdditionalProperties ?? []).ToDictionary(
            kv => kv.Key,
            kv => kv.Value switch
            {
                JsonElement element => element.ValueKind == JsonValueKind.Number
                    ? element.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText(),
                bool flag => flag ? "true" : "false",
                IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => kv.Value?.ToString() ?? "null",
            });
}
