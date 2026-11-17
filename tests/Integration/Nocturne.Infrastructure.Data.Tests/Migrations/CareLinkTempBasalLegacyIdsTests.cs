using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>CareLinkTempBasalLegacyIds</c> over two tenants holding live CareLink temp basals, the
/// copies earlier syncs swept, a user deletion and another source's rows, then down and up again, on a
/// real PostgreSQL as the migrator, which row level security binds.
/// </summary>
public class CareLinkTempBasalLegacyIdsFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261113080358_TrackFailedReconcileBatch";

    private string _migratorConnectionString = string.Empty;

    internal const string CareLink = "carelink-connector";
    internal const string Glooko = "glooko-connector";

    internal const string FirstStart = "2026-06-10T11:54:39Z";
    internal const string SecondStart = "2026-06-10T11:59:39Z";
    internal const string ThirdStart = "2026-06-10T12:04:39Z";
    internal const string FourthStart = "2026-06-10T12:09:39Z";
    internal const string FirstKey = "carelink_autobasal_1781092479000";
    internal const string SecondKey = "carelink_autobasal_1781092779000";
    internal const string FourthKey = "carelink_autobasal_1781093379000";

    internal static readonly DateTime Older = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Newer = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly Guid LiveInA = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    internal static readonly Guid SweptHeadInA = Guid.Parse("c0000000-0000-7000-8000-000000000002");
    internal static readonly Guid SweptMemberInA = Guid.Parse("c0000000-0000-7000-8000-000000000003");
    internal static readonly Guid OlderLiveTwinInA = Guid.Parse("c0000000-0000-7000-8000-000000000004");
    internal static readonly Guid NewerLiveTwinInA = Guid.Parse("c0000000-0000-7000-8000-000000000005");
    internal static readonly Guid UserDeletedInA = Guid.Parse("c0000000-0000-7000-8000-000000000006");
    internal static readonly Guid GlookoLiveInA = Guid.Parse("c0000000-0000-7000-8000-000000000007");
    internal static readonly Guid GlookoSweptInA = Guid.Parse("c0000000-0000-7000-8000-000000000008");
    internal static readonly Guid SweptUnderGlookoInA = Guid.Parse("c0000000-0000-7000-8000-000000000009");
    internal static readonly Guid LiveInB = Guid.Parse("c0000000-0000-7000-8000-00000000000a");
    internal static readonly Guid SweptAloneInB = Guid.Parse("c0000000-0000-7000-8000-00000000000b");
    internal static readonly Guid HeadingLosingTwinInA = Guid.Parse("c0000000-0000-7000-8000-00000000000c");
    internal static readonly Guid WinningTwinInA = Guid.Parse("c0000000-0000-7000-8000-00000000000d");
    internal static readonly Guid GlookoMemberInA = Guid.Parse("c0000000-0000-7000-8000-00000000000e");
    internal static readonly Guid SweptHeadOfUserDeletedInA = Guid.Parse("c0000000-0000-7000-8000-00000000000f");
    internal static readonly Guid UserDeletedMemberInA = Guid.Parse("c0000000-0000-7000-8000-000000000010");

    internal static readonly Guid GroupHeadedBySweptInA = Guid.Parse("d0000000-0000-7000-8000-000000000001");
    internal static readonly Guid GroupHeadedByGlookoInA = Guid.Parse("d0000000-0000-7000-8000-000000000002");
    internal static readonly Guid GroupOfSweptInB = Guid.Parse("d0000000-0000-7000-8000-000000000003");
    internal static readonly Guid GroupHeadedByLosingTwinInA = Guid.Parse("d0000000-0000-7000-8000-000000000004");
    internal static readonly Guid GroupOfUserDeletedInA = Guid.Parse("d0000000-0000-7000-8000-000000000005");

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];
    internal Dictionary<Guid, Row> AfterSecondUp { get; } = [];
    internal Dictionary<Guid, Link> LinksSeeded { get; } = [];
    internal Dictionary<Guid, Link> LinksAfterUp { get; } = [];
    internal Dictionary<Guid, Link> LinksAfterSecondUp { get; } = [];

    internal sealed record Row(
        Guid Tenant,
        string? DataSource,
        string? LegacyId,
        string? SyncIdentifier,
        DateTime? DeletedAt,
        bool DeletedByUser,
        DateTime SysUpdatedAt,
        bool IsDuplicate);

    internal sealed record Link(Guid Tenant, Guid CanonicalId, bool IsPrimary);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(
            PriorMigration, "carelink_temp_basal_legacy_ids");
        _migratorConnectionString = database.MigratorConnectionString;

        await SeedAsync();
        await ReadAsync(Seeded, LinksSeeded);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterUp, LinksAfterUp);
        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterSecondUp, LinksAfterSecondUp);
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

    private async Task ReadAsync(Dictionary<Guid, Row> rows, Dictionary<Guid, Link> links)
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
                    SELECT id, data_source, legacy_id, sync_identifier, deleted_at, deleted_by_user,
                           sys_updated_at, is_duplicate
                    FROM temp_basals;
                    """;
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rows[reader.GetGuid(0)] = new Row(
                        tenant,
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                        reader.GetBoolean(5),
                        reader.GetDateTime(6),
                        reader.GetBoolean(7));
            }

            await using var linkCmd = conn.CreateCommand();
            linkCmd.CommandText = """
                SELECT record_id, canonical_id, is_primary FROM linked_records WHERE record_type = 'tempbasal';
                """;
            await using var linkReader = await linkCmd.ExecuteReaderAsync();
            while (await linkReader.ReadAsync())
                links[linkReader.GetGuid(0)] = new Link(tenant, linkReader.GetGuid(1), linkReader.GetBoolean(2));
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
            INSERT INTO temp_basals
              (id, tenant_id, data_source, start_timestamp, rate, origin, legacy_id, sync_identifier,
               sys_updated_at, deleted_at, deleted_by_user, is_duplicate)
            VALUES
              ('{{LiveInA}}', '{{TenantA}}', '{{CareLink}}', '{{FirstStart}}', 1.2, 'Algorithm', NULL, NULL,
               '{{Older:O}}', NULL, false, true),
              ('{{SweptHeadInA}}', '{{TenantA}}', '{{CareLink}}', '{{FirstStart}}', 1.2, 'Algorithm', NULL, NULL,
               '{{Older:O}}', '{{Older:O}}', false, false),
              ('{{SweptMemberInA}}', '{{TenantA}}', '{{CareLink}}', '{{FirstStart}}', 1.2, 'Algorithm', NULL, NULL,
               '{{Older:O}}', '{{Older:O}}', false, true),
              ('{{OlderLiveTwinInA}}', '{{TenantA}}', '{{CareLink}}', '{{SecondStart}}', 0.9, 'Algorithm', NULL, NULL,
               '{{Older:O}}', NULL, false, false),
              ('{{NewerLiveTwinInA}}', '{{TenantA}}', '{{CareLink}}', '{{SecondStart}}', 1.0, 'Algorithm', NULL, NULL,
               '{{Newer:O}}', NULL, false, false),
              ('{{UserDeletedInA}}', '{{TenantA}}', '{{CareLink}}', '{{ThirdStart}}', 0.4, 'Algorithm', NULL, NULL,
               '{{Older:O}}', '{{Older:O}}', true, false),
              ('{{GlookoLiveInA}}', '{{TenantA}}', '{{Glooko}}', '{{FirstStart}}', 1.2, 'Algorithm',
               'glooko_tempbasal_1', NULL, '{{Older:O}}', NULL, false, false),
              ('{{GlookoSweptInA}}', '{{TenantA}}', '{{Glooko}}', '{{SecondStart}}', 1.0, 'Algorithm',
               'glooko_tempbasal_2', NULL, '{{Older:O}}', '{{Older:O}}', false, false),
              ('{{SweptUnderGlookoInA}}', '{{TenantA}}', '{{CareLink}}', '{{FirstStart}}', 1.2, 'Algorithm', NULL, NULL,
               '{{Older:O}}', '{{Older:O}}', false, true),
              ('{{HeadingLosingTwinInA}}', '{{TenantA}}', '{{CareLink}}', '{{FourthStart}}', 0.7, 'Algorithm', NULL, NULL,
               '{{Older:O}}', NULL, false, false),
              ('{{WinningTwinInA}}', '{{TenantA}}', '{{CareLink}}', '{{FourthStart}}', 0.8, 'Algorithm', NULL, NULL,
               '{{Newer:O}}', NULL, false, false),
              ('{{GlookoMemberInA}}', '{{TenantA}}', '{{Glooko}}', '{{FourthStart}}', 0.7, 'Algorithm',
               'glooko_tempbasal_3', NULL, '{{Older:O}}', NULL, false, true),
              ('{{SweptHeadOfUserDeletedInA}}', '{{TenantA}}', '{{CareLink}}', '{{ThirdStart}}', 0.5, 'Algorithm', NULL, NULL,
               '{{Older:O}}', '{{Older:O}}', false, false),
              ('{{UserDeletedMemberInA}}', '{{TenantA}}', '{{CareLink}}', '{{ThirdStart}}', 0.5, 'Algorithm', NULL, NULL,
               '{{Older:O}}', '{{Older:O}}', true, true);
            INSERT INTO linked_records
              (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary)
            VALUES
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupHeadedBySweptInA}}', 'tempbasal', '{{SweptHeadInA}}',
               1781092479000, '{{CareLink}}', true),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupHeadedBySweptInA}}', 'tempbasal', '{{SweptMemberInA}}',
               1781092479000, '{{CareLink}}', false),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupHeadedBySweptInA}}', 'tempbasal', '{{LiveInA}}',
               1781092479000, '{{CareLink}}', false),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupHeadedByGlookoInA}}', 'tempbasal', '{{GlookoLiveInA}}',
               1781092479000, '{{Glooko}}', true),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupHeadedByGlookoInA}}', 'tempbasal', '{{SweptUnderGlookoInA}}',
               1781092479000, '{{CareLink}}', false),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupHeadedByLosingTwinInA}}', 'tempbasal', '{{HeadingLosingTwinInA}}',
               1781093379000, '{{CareLink}}', true),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupHeadedByLosingTwinInA}}', 'tempbasal', '{{GlookoMemberInA}}',
               1781093379000, '{{Glooko}}', false),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupOfUserDeletedInA}}', 'tempbasal', '{{SweptHeadOfUserDeletedInA}}',
               1781093079000, '{{CareLink}}', true),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', '{{GroupOfUserDeletedInA}}', 'tempbasal', '{{UserDeletedMemberInA}}',
               1781093079000, '{{CareLink}}', false);

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO temp_basals
              (id, tenant_id, data_source, start_timestamp, rate, origin, legacy_id, sync_identifier,
               sys_updated_at, deleted_at, deleted_by_user, is_duplicate)
            VALUES
              ('{{LiveInB}}', '{{TenantB}}', '{{CareLink}}', '{{FirstStart}}', 1.2, 'Algorithm', NULL, NULL,
               '{{Older:O}}', NULL, false, false),
              ('{{SweptAloneInB}}', '{{TenantB}}', '{{CareLink}}', '{{FirstStart}}', 1.2, 'Algorithm', NULL, NULL,
               '{{Older:O}}', '{{Older:O}}', false, false);
            INSERT INTO linked_records
              (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary)
            VALUES
              ('{{Guid.CreateVersion7()}}', '{{TenantB}}', '{{GroupOfSweptInB}}', 'tempbasal', '{{SweptAloneInB}}',
               1781092479000, '{{CareLink}}', true);

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="CareLinkTempBasalLegacyIdsFixture"/>
[Trait("Category", "Integration")]
public class CareLinkTempBasalLegacyIdsTests(CareLinkTempBasalLegacyIdsFixture fixture)
    : IClassFixture<CareLinkTempBasalLegacyIdsFixture>
{
    [Theory]
    [MemberData(nameof(Keyed))]
    public void A_live_carelink_temp_basal_carries_the_key_the_mapper_derives_from_its_start(Guid id, string key)
    {
        var after = fixture.AfterUp[id];

        after.LegacyId.Should().Be(key);
        after.SyncIdentifier.Should().Be(key);
        after.DeletedAt.Should().BeNull();
    }

    public static TheoryData<Guid, string> Keyed() => new()
    {
        { CareLinkTempBasalLegacyIdsFixture.LiveInA, CareLinkTempBasalLegacyIdsFixture.FirstKey },
        { CareLinkTempBasalLegacyIdsFixture.NewerLiveTwinInA, CareLinkTempBasalLegacyIdsFixture.SecondKey },
        { CareLinkTempBasalLegacyIdsFixture.LiveInB, CareLinkTempBasalLegacyIdsFixture.FirstKey },
        { CareLinkTempBasalLegacyIdsFixture.WinningTwinInA, CareLinkTempBasalLegacyIdsFixture.FourthKey },
    };

    [Fact]
    public void Each_tenant_keeps_one_live_carelink_temp_basal_per_start()
    {
        fixture.AfterUp.Values
            .Where(row => row.DataSource == CareLinkTempBasalLegacyIdsFixture.CareLink && row.DeletedAt is null)
            .GroupBy(row => (row.Tenant, row.LegacyId))
            .Should().OnlyContain(group => group.Count() == 1);
    }

    [Theory]
    [MemberData(nameof(Removed))]
    public void A_swept_copy_or_older_live_twin_is_gone_with_its_link(Guid id)
    {
        fixture.Seeded.Should().ContainKey(id);
        fixture.AfterUp.Should().NotContainKey(id);
        fixture.LinksAfterUp.Should().NotContainKey(id);
    }

    public static TheoryData<Guid> Removed() =>
    [
        CareLinkTempBasalLegacyIdsFixture.SweptHeadInA,
        CareLinkTempBasalLegacyIdsFixture.SweptMemberInA,
        CareLinkTempBasalLegacyIdsFixture.OlderLiveTwinInA,
        CareLinkTempBasalLegacyIdsFixture.SweptUnderGlookoInA,
        CareLinkTempBasalLegacyIdsFixture.SweptAloneInB,
        CareLinkTempBasalLegacyIdsFixture.HeadingLosingTwinInA,
        CareLinkTempBasalLegacyIdsFixture.SweptHeadOfUserDeletedInA,
    ];

    [Theory]
    [MemberData(nameof(Untouched))]
    public void User_deletions_and_other_sources_are_untouched(Guid id)
    {
        fixture.AfterUp[id].Should().Be(fixture.Seeded[id]);
    }

    public static TheoryData<Guid> Untouched() =>
    [
        CareLinkTempBasalLegacyIdsFixture.UserDeletedInA,
        CareLinkTempBasalLegacyIdsFixture.GlookoLiveInA,
        CareLinkTempBasalLegacyIdsFixture.GlookoSweptInA,
    ];

    [Fact]
    public void A_group_a_swept_copy_headed_is_headed_by_its_live_member()
    {
        fixture.LinksAfterUp.Values
            .Where(link => link.CanonicalId == CareLinkTempBasalLegacyIdsFixture.GroupHeadedBySweptInA)
            .Should().ContainSingle();
        fixture.LinksAfterUp[CareLinkTempBasalLegacyIdsFixture.LiveInA].IsPrimary.Should().BeTrue();
        fixture.AfterUp[CareLinkTempBasalLegacyIdsFixture.LiveInA].IsDuplicate.Should().BeFalse();
    }

    [Fact]
    public void A_group_a_losing_live_twin_headed_is_headed_by_its_remaining_member()
    {
        fixture.LinksAfterUp.Values
            .Where(link => link.CanonicalId == CareLinkTempBasalLegacyIdsFixture.GroupHeadedByLosingTwinInA)
            .Should().ContainSingle();
        fixture.LinksAfterUp[CareLinkTempBasalLegacyIdsFixture.GlookoMemberInA].IsPrimary.Should().BeTrue();
        var member = fixture.AfterUp[CareLinkTempBasalLegacyIdsFixture.GlookoMemberInA];
        member.IsDuplicate.Should().BeFalse();
        member.DeletedAt.Should().BeNull();
    }

    [Fact]
    public void A_group_left_with_only_a_user_deletion_is_headed_by_it_and_it_stays_deleted()
    {
        fixture.LinksAfterUp.Values
            .Where(link => link.CanonicalId == CareLinkTempBasalLegacyIdsFixture.GroupOfUserDeletedInA)
            .Should().ContainSingle();
        fixture.LinksAfterUp[CareLinkTempBasalLegacyIdsFixture.UserDeletedMemberInA].IsPrimary.Should().BeTrue();
        var member = fixture.AfterUp[CareLinkTempBasalLegacyIdsFixture.UserDeletedMemberInA];
        member.IsDuplicate.Should().BeFalse();
        member.DeletedByUser.Should().BeTrue();
        member.DeletedAt.Should().Be(fixture.Seeded[CareLinkTempBasalLegacyIdsFixture.UserDeletedMemberInA].DeletedAt);
    }

    [Fact]
    public void A_group_another_source_heads_keeps_its_primary()
    {
        fixture.LinksAfterUp[CareLinkTempBasalLegacyIdsFixture.GlookoLiveInA]
            .Should().Be(fixture.LinksSeeded[CareLinkTempBasalLegacyIdsFixture.GlookoLiveInA]);
    }

    [Fact]
    public void Down_and_up_again_changes_nothing()
    {
        fixture.AfterSecondUp.Should().BeEquivalentTo(fixture.AfterUp);
        fixture.LinksAfterSecondUp.Should().BeEquivalentTo(fixture.LinksAfterUp);
    }
}
