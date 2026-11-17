using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="ApsSnapshotRepository.GetPredictionsAsync"/> against PostgreSQL, read as
/// <c>nocturne_app</c>: the thinning is the <c>DISTINCT ON</c> the query sends, and the blob columns
/// it must not read are visible only in that statement.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ApsSnapshotPredictionProjectionTests(RlsCompletenessFixture fx)
{
    private static readonly DateTime Start = new(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc);
    private const string Blob = """{"reason":"a large decision blob"}""";

    private sealed record Row(
        DateTime At,
        string Device = "openaps://phone",
        bool Enacted = false,
        string? Iob = "[120,118]",
        string? Default = null,
        bool Deleted = false,
        DateTime? PredictedStart = null,
        bool NoPredictedStart = false,
        bool? ClosedLoop = null);

    [Fact]
    public async Task Thins_to_one_per_device_per_five_minutes_preferring_the_enacted()
    {
        var tenant = await SeedAsync(
            new Row(Start.AddMinutes(1), Iob: "[1]"),
            new Row(Start.AddMinutes(2), Enacted: true, Iob: "[2]"),
            new Row(Start.AddMinutes(4), Iob: "[3]"),
            new Row(Start.AddMinutes(1), Device: "openaps://other", Iob: "[4]"),
            new Row(Start.AddMinutes(6), Iob: "[5]"),
            new Row(Start.AddMinutes(7), Iob: "[6]"));

        var (points, _) = await ReadAsync(tenant);

        points.Select(p => (p.Device, p.IobJson)).Should().BeEquivalentTo(
            [("openaps://phone", "[2]"), ("openaps://other", "[4]"), ("openaps://phone", "[6]")],
            "the enacted snapshot wins its bucket over newer ones; with none enacted the newest wins");
        points.Select(p => p.PredictedStart).Should().BeInAscendingOrder();
        points.Should().OnlyContain(p => p.PredictedStart.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task Windows_and_buckets_on_the_prediction_start()
    {
        var tenant = await SeedAsync(
            new Row(Start.AddMinutes(6), Iob: "[1]", PredictedStart: Start.AddMinutes(4)),
            new Row(Start.AddMinutes(7), Iob: "[2]", PredictedStart: Start.AddMinutes(7)),
            new Row(Start.AddMinutes(8), Iob: "[3]", PredictedStart: Start.AddMinutes(3)),
            new Row(Start.AddMinutes(1), Iob: "[4]", PredictedStart: Start.AddMinutes(-1)),
            new Row(Start.AddHours(1).AddMinutes(2), Iob: "[5]", PredictedStart: Start.AddMinutes(58)));

        var (points, _) = await ReadAsync(tenant);

        points.Select(p => p.IobJson).Should().Equal(
            ["[3]", "[2]", "[5]"],
            "[3] shares [1]'s bucket by start though not by timestamp, [4] started before the window, and [5] within it though stored after");
    }

    [Fact]
    public async Task Drops_the_default_copy_beside_orefs_curves()
    {
        var tenant = await SeedAsync(new Row(Start.AddMinutes(1), Iob: "[120]", Default: "[120]"));

        var (points, _) = await ReadAsync(tenant);

        points.Should().ContainSingle().Which.DefaultJson.Should().BeNull();
    }

    [Fact]
    public async Task Leaves_out_snapshots_without_curves_deleted_or_outside_the_window()
    {
        var tenant = await SeedAsync(
            new Row(Start.AddMinutes(1), Iob: null),
            new Row(Start.AddMinutes(6), Deleted: true),
            new Row(Start.AddMinutes(11), NoPredictedStart: true),
            new Row(Start.AddHours(-1)),
            new Row(Start.AddDays(1)),
            new Row(Start.AddMinutes(16), Iob: null, Default: "[130,125]"));

        var (points, _) = await ReadAsync(tenant);

        var only = points.Should().ContainSingle().Subject;
        only.DefaultJson.Should().Be("[130, 125]", "PostgreSQL normalises the jsonb it returns");
        only.AidAlgorithm.Should().Be(AidAlgorithm.AndroidAps);
    }

    [Fact]
    public async Task Reads_only_the_tenants_own_rows()
    {
        var mine = await SeedAsync(new Row(Start.AddMinutes(1), Iob: "[1]"));
        await SeedAsync(new Row(Start.AddMinutes(1), Iob: "[2]"));

        var (points, _) = await ReadAsync(mine);

        points.Select(p => p.IobJson).Should().Equal("[1]");
    }

    [Fact]
    public async Task Does_not_read_the_json_blobs()
    {
        var tenant = await SeedAsync(new Row(Start.AddMinutes(1)));

        var (points, statements) = await ReadAsync(tenant);

        points.Should().ContainSingle();
        var query = statements.Should().ContainSingle(s => s.Contains("aps_snapshots")).Subject;
        query.Should().NotContain("suggested_json").And.NotContain("enacted_json").And.NotContain("loop_json")
            .And.NotContain("additional_properties").And.NotContain("*");
    }

    [Fact]
    public async Task Iob_cob_points_carry_each_snapshots_closed_loop_state()
    {
        var tenant = await SeedAsync(
            new Row(Start.AddMinutes(1), ClosedLoop: false),
            new Row(Start.AddMinutes(2), ClosedLoop: true),
            new Row(Start.AddMinutes(3), ClosedLoop: null));

        var points = await ReadAsync(tenant, (repository, from, to) => repository.GetIobCobPointsAsync(from, to));

        points.Select(p => p.ClosedLoop).Should().Equal(false, true, null);
    }

    private async Task<Guid> SeedAsync(params Row[] rows)
    {
        var tenant = Guid.NewGuid();
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'prediction-projection-test', true, now(), now());
                SELECT set_config('app.current_tenant_id', @tid::text, false);
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"pred-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var row in rows)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted,
                    predicted_start_timestamp, predicted_default_json, predicted_iob_json,
                    suggested_json, enacted_json, loop_json, closed_loop, deleted_at, sys_created_at, sys_updated_at)
                VALUES (gen_random_uuid(), @tid, @at, @device, 'AndroidAps', @enacted,
                    @predictedStart, @default::jsonb, @iob::jsonb,
                    @blob::jsonb, @blob::jsonb, @blob::jsonb, @closedLoop, @deleted, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("at", row.At);
            cmd.Parameters.AddWithValue("device", row.Device);
            cmd.Parameters.AddWithValue("enacted", row.Enacted);
            cmd.Parameters.AddWithValue("predictedStart", row.NoPredictedStart ? DBNull.Value : row.PredictedStart ?? row.At);
            cmd.Parameters.AddWithValue("default", (object?)row.Default ?? DBNull.Value);
            cmd.Parameters.AddWithValue("iob", (object?)row.Iob ?? DBNull.Value);
            cmd.Parameters.AddWithValue("blob", Blob);
            cmd.Parameters.AddWithValue("closedLoop", (object?)row.ClosedLoop ?? DBNull.Value);
            cmd.Parameters.AddWithValue("deleted", row.Deleted ? DateTime.UtcNow : DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        return tenant;
    }

    private async Task<(IReadOnlyList<ApsPredictionPoint> Points, IReadOnlyList<string> Statements)> ReadAsync(Guid tenant)
    {
        var recorder = new StatementRecorder();
        var points = await ReadAsync(tenant, (repository, from, to) => repository.GetPredictionsAsync(from, to), recorder);
        return (points, recorder.All);
    }

    private async Task<T> ReadAsync<T>(
        Guid tenant, Func<ApsSnapshotRepository, DateTime, DateTime, Task<T>> read, StatementRecorder? recorder = null)
    {
        await using var conn = await fx.OpenAppConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder ?? new StatementRecorder())
            .Options) { TenantId = tenant };
        var repository = new ApsSnapshotRepository(
            new TestNocturneDbContextFactory(ctx), new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance);

        return await read(repository, Start, Start.AddHours(1));
    }
}
