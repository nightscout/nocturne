using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>KeyStateSpansOnLegacyId</c> over two tenants holding live state spans that share an
/// <c>original_id</c>, then down and up again, on a real PostgreSQL as the migrator, which row level
/// security binds.
/// </summary>
public class KeyStateSpansOnLegacyIdFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261102113113_AddDeduplicationRunState";

    private string _migratorConnectionString = string.Empty;

    internal static readonly DateTime Older = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Newer = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Newest = new(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly Guid PrimaryOlderInA = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    internal static readonly Guid PrimaryNewerInA = Guid.Parse("c0000000-0000-7000-8000-000000000002");
    internal static readonly Guid DuplicateNewestInA = Guid.Parse("c0000000-0000-7000-8000-000000000003");
    internal static readonly Guid UserTombstoneInA = Guid.Parse("c0000000-0000-7000-8000-000000000004");
    internal static readonly Guid SoloInA = Guid.Parse("c0000000-0000-7000-8000-000000000005");
    internal static readonly Guid UnkeyedInA = Guid.Parse("c0000000-0000-7000-8000-000000000006");
    internal static readonly Guid OtherUnkeyedInA = Guid.Parse("c0000000-0000-7000-8000-000000000007");
    internal static readonly Guid SameIdInB = Guid.Parse("c0000000-0000-7000-8000-000000000008");
    internal static readonly Guid TiedLowerIdInB = Guid.Parse("c0000000-0000-7000-8000-000000000009");
    internal static readonly Guid TiedHigherIdInB = Guid.Parse("c0000000-0000-7000-8000-00000000000a");
    internal static readonly Guid SupersededByLoserInA = Guid.Parse("c0000000-0000-7000-8000-00000000000b");
    internal static readonly Guid EarlierMemberInA = Guid.Parse("c0000000-0000-7000-8000-00000000000c");
    internal static readonly Guid LiveMemberInB = Guid.Parse("c0000000-0000-7000-8000-00000000000d");
    internal static readonly Guid DeletedMemberInB = Guid.Parse("c0000000-0000-7000-8000-00000000000e");

    internal static readonly Guid GroupWithWinnerInA = Guid.Parse("d0000000-0000-7000-8000-000000000001");
    internal static readonly Guid GroupWithoutWinnerInB = Guid.Parse("d0000000-0000-7000-8000-000000000002");

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];
    internal Dictionary<Guid, Row> AfterSecondUp { get; } = [];
    internal Dictionary<Guid, Guid[]> PrimariesAfterUp { get; } = [];
    internal Dictionary<Guid, Guid[]> PrimariesAfterSecondUp { get; } = [];
    internal (bool Unique, bool Valid, string Definition)? LegacyIdIndex { get; private set; }
    internal (bool Unique, bool Valid, string Definition)? AnyLegacyIdIndex { get; private set; }
    internal bool OriginalIdIndexExists { get; private set; }
    internal string? SecondLiveSoloSqlState { get; private set; }

    internal sealed record Row(
        Guid Tenant,
        string? OriginalId,
        DateTime? DeletedAt,
        bool DeletedByUser,
        DateTime UpdatedAt,
        Guid? SupersededById,
        bool IsDuplicate);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "key_state_spans_on_legacy_id");
        _migratorConnectionString = database.MigratorConnectionString;

        await SeedAsync();
        await ReadAsync(Seeded);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterUp, PrimariesAfterUp);
        await ReadIndexesAsync();
        SecondLiveSoloSqlState = await InsertSecondLiveSoloAsync();
        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterSecondUp, PrimariesAfterSecondUp);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task ReadIndexesAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.relname, i.indisunique, i.indisvalid, pg_get_indexdef(i.indexrelid)
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname IN ('ix_state_spans_tenant_legacy_id', 'ix_state_spans_tenant_legacy_id_any', 'ix_state_spans_original_id');
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var index = (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3));
            switch (reader.GetString(0))
            {
                case "ix_state_spans_original_id":
                    OriginalIdIndexExists = true;
                    break;
                case "ix_state_spans_tenant_legacy_id_any":
                    AnyLegacyIdIndex = index;
                    break;
                default:
                    LegacyIdIndex = index;
                    break;
            }
        }
    }

    private async Task<string?> InsertSecondLiveSoloAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO state_spans (id, tenant_id, category, state, start_timestamp, source, original_id)
            VALUES ('{{Guid.CreateVersion7()}}', '{{TenantA}}', 'PumpMode', 'Manual', now(), 'Loop', 'solo');
            """;
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private async Task ReadAsync(Dictionary<Guid, Row> rows, Dictionary<Guid, Guid[]>? primaries = null)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, original_id, deleted_at, deleted_by_user, updated_at, superseded_by_id, is_duplicate
                    FROM state_spans;
                    """;
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rows[reader.GetGuid(0)] = new Row(
                        tenant,
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                        reader.GetBoolean(3),
                        reader.GetDateTime(4),
                        reader.IsDBNull(5) ? null : reader.GetGuid(5),
                        reader.GetBoolean(6));
            }

            if (primaries is null)
                continue;

            await using var links = conn.CreateCommand();
            links.CommandText = """
                SELECT canonical_id, array_agg(record_id ORDER BY record_id)
                FROM linked_records
                WHERE is_primary
                GROUP BY canonical_id;
                """;
            await using var linkReader = await links.ExecuteReaderAsync();
            while (await linkReader.ReadAsync())
                primaries[linkReader.GetGuid(0)] = linkReader.GetFieldValue<Guid[]>(1);
        }
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, original_id, is_duplicate,
               created_at, updated_at, deleted_at, deleted_by_user)
            VALUES
              ('{{PrimaryOlderInA}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T08:00:00Z', 'Loop', 'dup', false,
               now(), '{{Older:O}}', NULL, false),
              ('{{PrimaryNewerInA}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T08:00:00Z', 'Loop', 'dup', false,
               now(), '{{Newer:O}}', NULL, false),
              ('{{DuplicateNewestInA}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T08:00:00Z', 'AAPS', 'dup', true,
               now(), '{{Newest:O}}', NULL, false),
              ('{{UserTombstoneInA}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T08:00:00Z', 'Loop', 'dup', false,
               now(), '{{Older:O}}', '{{Older:O}}', true),
              ('{{SoloInA}}', '{{TenantA}}', 'PumpMode', 'Automatic', '2026-06-01T09:00:00Z', 'Loop', 'solo', false,
               now(), '{{Older:O}}', NULL, false),
              ('{{UnkeyedInA}}', '{{TenantA}}', 'PumpMode', 'Manual', '2026-06-01T10:00:00Z', 'Loop', NULL, false,
               now(), '{{Older:O}}', NULL, false),
              ('{{OtherUnkeyedInA}}', '{{TenantA}}', 'PumpMode', 'Manual', '2026-06-01T11:00:00Z', 'Loop', NULL, false,
               now(), '{{Older:O}}', NULL, false),
              ('{{EarlierMemberInA}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T08:00:00Z', 'xDrip', NULL, true,
               now(), '{{Older:O}}', NULL, false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, original_id, is_duplicate,
               created_at, updated_at, deleted_at, deleted_by_user, superseded_by_id)
            VALUES
              ('{{SupersededByLoserInA}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T07:00:00Z', 'Loop', NULL, false,
               now(), '{{Older:O}}', NULL, false, '{{PrimaryOlderInA}}');
            INSERT INTO linked_records
              (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary)
            VALUES
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupWithWinnerInA}}', 'statespan', '{{PrimaryOlderInA}}',
               1780300800000, 'Loop', true),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupWithWinnerInA}}', 'statespan', '{{EarlierMemberInA}}',
               1780300700000, 'xDrip', false),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupWithWinnerInA}}', 'statespan', '{{PrimaryNewerInA}}',
               1780300900000, 'Loop', false);

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, original_id, is_duplicate,
               created_at, updated_at, deleted_at, deleted_by_user)
            VALUES
              ('{{SameIdInB}}', '{{TenantB}}', 'Override', 'Custom', '2026-06-01T08:00:00Z', 'AAPS', 'dup', false,
               now(), '{{Older:O}}', NULL, false),
              ('{{TiedLowerIdInB}}', '{{TenantB}}', 'Exercise', 'Running', '2026-06-02T08:00:00Z', 'AAPS', 'tied', false,
               now(), '{{Older:O}}', NULL, false),
              ('{{TiedHigherIdInB}}', '{{TenantB}}', 'Exercise', 'Running', '2026-06-02T08:00:00Z', 'AAPS', 'tied', false,
               now(), '{{Older:O}}', NULL, false),
              ('{{LiveMemberInB}}', '{{TenantB}}', 'Exercise', 'Running', '2026-06-02T08:00:00Z', 'Loop', NULL, true,
               now(), '{{Older:O}}', NULL, false),
              ('{{DeletedMemberInB}}', '{{TenantB}}', 'Exercise', 'Running', '2026-06-02T08:00:00Z', 'xDrip', NULL, true,
               now(), '{{Older:O}}', '{{Older:O}}', false);
            INSERT INTO linked_records
              (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary)
            VALUES
              ('{{Guid.CreateVersion7()}}', '{{TenantB}}', '{{GroupWithoutWinnerInB}}', 'statespan', '{{TiedLowerIdInB}}',
               1780387200000, 'AAPS', true),
              ('{{Guid.CreateVersion7()}}', '{{TenantB}}', '{{GroupWithoutWinnerInB}}', 'statespan', '{{DeletedMemberInB}}',
               1780387100000, 'xDrip', false),
              ('{{Guid.CreateVersion7()}}', '{{TenantB}}', '{{GroupWithoutWinnerInB}}', 'statespan', '{{LiveMemberInB}}',
               1780387300000, 'Loop', false);

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="KeyStateSpansOnLegacyIdFixture"/>
[Trait("Category", "Integration")]
public class KeyStateSpansOnLegacyIdTests(KeyStateSpansOnLegacyIdFixture fixture)
    : IClassFixture<KeyStateSpansOnLegacyIdFixture>
{
    [Fact]
    public void Each_tenant_keeps_one_live_span_per_original_id()
    {
        fixture.AfterUp.Values
            .Where(row => row.DeletedAt is null && row.OriginalId is not null)
            .GroupBy(row => (row.Tenant, row.OriginalId))
            .Should().OnlyContain(group => group.Count() == 1);
    }

    [Theory]
    [MemberData(nameof(Losers))]
    public void A_loser_is_soft_deleted_by_the_system_without_moving_its_history_clock(Guid id)
    {
        var after = fixture.AfterUp[id];

        fixture.Seeded[id].DeletedAt.Should().BeNull();
        after.DeletedAt.Should().NotBeNull();
        after.DeletedByUser.Should().BeFalse("a system delete leaves the id re-importable");
        after.UpdatedAt.Should().Be(fixture.Seeded[id].UpdatedAt);
    }

    public static TheoryData<Guid> Losers() =>
    [
        KeyStateSpansOnLegacyIdFixture.PrimaryOlderInA,
        KeyStateSpansOnLegacyIdFixture.DuplicateNewestInA,
        KeyStateSpansOnLegacyIdFixture.TiedLowerIdInB,
    ];

    [Theory]
    [MemberData(nameof(Untouched))]
    public void Winners_unique_ids_unkeyed_spans_and_tombstones_are_untouched(Guid id)
    {
        fixture.AfterUp[id].Should().Be(fixture.Seeded[id]);
    }

    public static TheoryData<Guid> Untouched() =>
    [
        KeyStateSpansOnLegacyIdFixture.PrimaryNewerInA,
        KeyStateSpansOnLegacyIdFixture.UserTombstoneInA,
        KeyStateSpansOnLegacyIdFixture.SoloInA,
        KeyStateSpansOnLegacyIdFixture.UnkeyedInA,
        KeyStateSpansOnLegacyIdFixture.OtherUnkeyedInA,
        KeyStateSpansOnLegacyIdFixture.SameIdInB,
        KeyStateSpansOnLegacyIdFixture.TiedHigherIdInB,
        KeyStateSpansOnLegacyIdFixture.EarlierMemberInA,
        KeyStateSpansOnLegacyIdFixture.DeletedMemberInB,
    ];

    [Fact]
    public void A_span_superseded_by_a_loser_is_superseded_by_its_winner_and_sent_to_history_clients()
    {
        var seeded = fixture.Seeded[KeyStateSpansOnLegacyIdFixture.SupersededByLoserInA];
        var after = fixture.AfterUp[KeyStateSpansOnLegacyIdFixture.SupersededByLoserInA];

        seeded.SupersededById.Should().Be(KeyStateSpansOnLegacyIdFixture.PrimaryOlderInA);
        after.SupersededById.Should().Be(KeyStateSpansOnLegacyIdFixture.PrimaryNewerInA);
        after.UpdatedAt.Should().BeAfter(seeded.UpdatedAt);
        after.DeletedAt.Should().BeNull();
    }

    [Fact]
    public void A_group_a_loser_headed_is_headed_by_its_winner_when_the_winner_is_a_member()
    {
        fixture.PrimariesAfterUp[KeyStateSpansOnLegacyIdFixture.GroupWithWinnerInA]
            .Should().Equal(KeyStateSpansOnLegacyIdFixture.PrimaryNewerInA);
        fixture.AfterUp[KeyStateSpansOnLegacyIdFixture.PrimaryNewerInA].IsDuplicate.Should().BeFalse();
        fixture.AfterUp[KeyStateSpansOnLegacyIdFixture.PrimaryOlderInA].IsDuplicate.Should().BeTrue();
    }

    [Fact]
    public void A_group_a_loser_headed_without_its_winner_is_headed_by_a_live_member()
    {
        fixture.PrimariesAfterUp[KeyStateSpansOnLegacyIdFixture.GroupWithoutWinnerInB]
            .Should().Equal(KeyStateSpansOnLegacyIdFixture.LiveMemberInB);
        fixture.AfterUp[KeyStateSpansOnLegacyIdFixture.LiveMemberInB].IsDuplicate.Should().BeFalse();
        fixture.AfterUp[KeyStateSpansOnLegacyIdFixture.TiedLowerIdInB].IsDuplicate.Should().BeTrue();
        fixture.AfterUp[KeyStateSpansOnLegacyIdFixture.LiveMemberInB].UpdatedAt
            .Should().Be(fixture.Seeded[KeyStateSpansOnLegacyIdFixture.LiveMemberInB].UpdatedAt);
    }

    [Fact]
    public void The_live_original_id_index_is_unique_valid_and_replaces_the_plain_one()
    {
        fixture.LegacyIdIndex.Should().NotBeNull();
        fixture.LegacyIdIndex!.Value.Unique.Should().BeTrue();
        fixture.LegacyIdIndex.Value.Valid.Should().BeTrue();
        fixture.LegacyIdIndex.Value.Definition.Should().Contain("(tenant_id, original_id)")
            .And.Contain("deleted_at IS NULL");
        fixture.OriginalIdIndexExists.Should().BeFalse();
    }

    [Fact]
    public void Deleted_spans_of_any_kind_are_found_by_id_through_an_unfiltered_index()
    {
        fixture.AnyLegacyIdIndex.Should().NotBeNull();
        fixture.AnyLegacyIdIndex!.Value.Unique.Should().BeFalse();
        fixture.AnyLegacyIdIndex.Value.Valid.Should().BeTrue();
        fixture.AnyLegacyIdIndex.Value.Definition.Should().Contain("(tenant_id, original_id)")
            .And.NotContain("WHERE");
    }

    [Fact]
    public void A_second_live_span_under_a_held_id_is_refused()
    {
        fixture.SecondLiveSoloSqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public void Down_and_up_again_deletes_nothing_more()
    {
        fixture.AfterSecondUp.Should().BeEquivalentTo(fixture.AfterUp);
        fixture.PrimariesAfterSecondUp.Should().BeEquivalentTo(fixture.PrimariesAfterUp);
    }
}
