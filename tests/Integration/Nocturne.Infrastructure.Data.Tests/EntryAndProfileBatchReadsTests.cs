using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The reads and writes an entry or profile batch makes, against PostgreSQL as <c>nocturne_app</c>:
/// how many statements each is whatever it is asked for.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class EntryAndProfileBatchReadsTests(RlsCompletenessFixture fx)
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetCorrelationIdsByLegacyIdAsync_ReadsEveryIdInOneStatement()
    {
        await using var store = await OpenAsync();
        var readings = Readings(store);
        await readings.BulkCreateAsync(
            [.. Enumerable.Range(0, 120).Select(i => Reading($"sg-{i}", i, correlationId: Guid.CreateVersion7()))],
            WriteOrigin.Live);
        store.Recorder.Clear();

        var found = await readings.GetCorrelationIdsByLegacyIdAsync([.. Enumerable.Range(0, 100).Select(i => $"sg-{i}"), "absent"]);

        store.Recorder.Count("SELECT").Should().Be(1);
        found.Should().HaveCount(100);
    }

    /// <summary>
    /// The read an entry batch makes for its served-shaped ids, an upload's write-back echoes or an
    /// edit's id: one statement per fifty ids, finding the reading a uuid prefix names.
    /// </summary>
    [Fact]
    public async Task GetByServedIdsAsync_OfReadings_ReadsFiftyIdsPerStatement()
    {
        await using var store = await OpenAsync();
        var readings = Readings(store);
        var unkeyed = (await readings.BulkCreateAsync([Reading(null, 0)], WriteOrigin.Live)).Single();
        store.Recorder.Clear();

        string[] ids =
        [
            MongoObjectId.FromGuid(unkeyed.Id),
            .. Enumerable.Range(1, 99).Select(i => $"5e00{i:x4}0000700080000000"),
        ];
        var found = await readings.GetByServedIdsAsync(ids);

        store.Recorder.Count("SELECT").Should().Be(2, "fifty served ids are read per statement");
        found.Select(r => r.Id).Should().Equal(unkeyed.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkUpsertAsync_OfReadings_RunsTheSameStatementsForOneAndFifty(bool resend)
    {
        var one = await UpsertStatementsAsync(1, resend);
        var fifty = await UpsertStatementsAsync(50, resend);

        fifty.Should().Equal(one);
    }

    private async Task<List<string>> UpsertStatementsAsync(int size, bool resend)
    {
        await using var store = await OpenAsync();
        var readings = Readings(store);
        List<SensorGlucose> Batch(double mgdl) => [.. Enumerable.Range(0, size).Select(i => Reading($"sg-{i}", i, mgdl))];
        if (resend)
            await readings.BulkUpsertAsync(Batch(100), WriteOrigin.Live);
        store.Recorder.Clear();

        await readings.BulkUpsertAsync(Batch(150), WriteOrigin.Live);

        return [.. store.Recorder.All.Select(s => s.Split(' ', 2)[0])];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkUpsertByLegacyIdAsync_OfSettings_RunsTheSameStatementsForOneAndFifty(bool resend)
    {
        var one = await SettingsStatementsAsync(1, resend);
        var fifty = await SettingsStatementsAsync(50, resend);

        fifty.Should().Equal(one);
    }

    private async Task<List<string>> SettingsStatementsAsync(int size, bool resend)
    {
        await using var store = await OpenAsync();
        var settings = new TherapySettingsRepository(store.Factory, new SystemAuditContext(), NullLogger<TherapySettingsRepository>.Instance);
        List<TherapySettings> Batch(double dia) =>
            [.. Enumerable.Range(0, size).Select(i => new TherapySettings
            {
                LegacyId = $"doc-{i}:Default", ProfileName = "Default", Timestamp = Start.AddHours(i), Dia = dia,
            })];
        if (resend)
            await settings.BulkUpsertByLegacyIdAsync(Batch(3), WriteOrigin.Live);
        store.Recorder.Clear();

        await settings.BulkUpsertByLegacyIdAsync(Batch(4), WriteOrigin.Live, preserveStoredCorrelationId: true);

        return [.. store.Recorder.All.Select(s => s.Split(' ', 2)[0])];
    }

    [Fact]
    public async Task ScheduleSiblingReads_ReadEveryRowInOneStatementEach()
    {
        await using var store = await OpenAsync();
        var basal = new BasalScheduleRepository(store.Factory, new SystemAuditContext(), NullLogger<BasalScheduleRepository>.Instance);
        var groups = Enumerable.Range(0, 30).Select(_ => Guid.CreateVersion7()).ToList();
        await basal.BulkCreateAsync(
            [.. groups.Select((g, i) => new BasalSchedule { LegacyId = $"doc-{i}:Default", ProfileName = "Default", Timestamp = Start.AddHours(i), CorrelationId = g })],
            WriteOrigin.Live);
        store.Recorder.Clear();

        var byCorrelation = await basal.GetByCorrelationIdsAsync(groups);
        var byLegacyId = await basal.GetByLegacyIdsAsync([.. Enumerable.Range(0, 30).Select(i => $"doc-{i}:Default")]);

        store.Recorder.Count("SELECT").Should().Be(2);
        byCorrelation.Should().HaveCount(30);
        byLegacyId.Should().HaveCount(30);
    }

    private static SensorGlucoseRepository Readings(Store store) =>
        new(store.Factory, Mock.Of<IDeduplicationService>(), new SystemAuditContext(), NullLogger<SensorGlucoseRepository>.Instance);

    private static SensorGlucose Reading(string? legacyId, int minute, double mgdl = 100, Guid? correlationId = null) => new()
    {
        LegacyId = legacyId, Timestamp = Start.AddMinutes(5 * minute), Mgdl = mgdl, Device = "xDrip", CorrelationId = correlationId,
    };

    private async Task<Store> OpenAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var migrator = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = migrator.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'entry-profile-batch-reads', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"epbr-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        var conn = await fx.OpenAppConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var recorder = new StatementRecorder();
        var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder)
            .Options) { TenantId = tenant };
        return new Store(conn, context, recorder);
    }

    private sealed class Store(NpgsqlConnection connection, NocturneDbContext context, StatementRecorder recorder) : IAsyncDisposable
    {
        public NocturneDbContext Context { get; } = context;
        public StatementRecorder Recorder { get; } = recorder;
        public INocturneDbContextFactory Factory { get; } = new TestNocturneDbContextFactory(context);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
