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
/// The set-based reads a treatment batch makes, against PostgreSQL as <c>nocturne_app</c>: what each
/// returns, and how many statements each is whatever it is asked for.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class TreatmentBatchReadsTests(RlsCompletenessFixture fx)
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetByLegacyIdsAsync_ReadsEveryLiveRowUnderTheIdsInOneStatement()
    {
        await using var store = await OpenAsync();
        var notes = Notes(store);
        await notes.BulkCreateAsync([.. Enumerable.Range(0, 120).Select(i => Note($"n-{i}", i))], WriteOrigin.Live);
        store.Recorder.Clear();

        var found = await notes.GetByLegacyIdsAsync([.. Enumerable.Range(0, 100).Select(i => $"n-{i}"), "absent"]);

        store.Recorder.Count("SELECT").Should().Be(1);
        found.Select(n => n.LegacyId).Should().HaveCount(100);
    }

    [Fact]
    public async Task GetByServedIdsAsync_FindsEachKeyOfEachIdInOneStatementPerFifty()
    {
        await using var store = await OpenAsync();
        var notes = Notes(store);
        var uuidLegacyId = Guid.CreateVersion7().ToString();
        var written = await notes.BulkCreateAsync(
        [
            Note("6a0000000000000000000001", 0),
            Note(uuidLegacyId, 1),
            Note("unrelated", 2),
            .. Enumerable.Range(0, 60).Select(i => Note($"filler-{i}", 10 + i)),
        ], WriteOrigin.Live);
        var byRecordId = written.Single(n => n.LegacyId == "unrelated");
        store.Recorder.Clear();

        string[] ids =
        [
            "6a0000000000000000000001",
            MongoObjectId.FromGuid(byRecordId.Id),
            uuidLegacyId.Replace("-", "")[..24],
            .. Enumerable.Range(0, 50).Select(_ => MongoObjectId.NewObjectId()),
        ];
        var found = await notes.GetByServedIdsAsync(ids);

        store.Recorder.Count("SELECT").Should().Be(2, "fifty served ids are read per statement");
        found.Select(n => n.LegacyId).Should().BeEquivalentTo(
            ["6a0000000000000000000001", "unrelated", uuidLegacyId],
            "a served id names a record by its legacy id, by the uuid it prefixes, or by a uuid legacy id it prefixes");
    }

    [Fact]
    public async Task GetByCorrelationIdsAsync_ReadsEveryMealHalfInOneStatement()
    {
        await using var store = await OpenAsync();
        var boluses = new BolusRepository(store.Factory, Mock.Of<IDeduplicationService>(), new SystemAuditContext(), NullLogger<BolusRepository>.Instance);
        var carbs = new CarbIntakeRepository(store.Factory, Mock.Of<IDeduplicationService>(), new SystemAuditContext(), NullLogger<CarbIntakeRepository>.Instance);
        var groups = Enumerable.Range(0, 30).Select(_ => Guid.CreateVersion7()).ToList();
        await boluses.BulkCreateAsync([.. groups.Select((g, i) => new Bolus { Timestamp = Start.AddMinutes(i), Insulin = 1, CorrelationId = g })], WriteOrigin.Live);
        await carbs.BulkCreateAsync([.. groups.Select((g, i) => new CarbIntake { Timestamp = Start.AddMinutes(i), Carbs = 10, CorrelationId = g })], WriteOrigin.Live);
        store.Recorder.Clear();

        var bolusHalves = await boluses.GetByCorrelationIdsAsync(groups);
        var carbHalves = await carbs.GetByCorrelationIdsAsync(groups);

        store.Recorder.Count("SELECT").Should().Be(2);
        bolusHalves.Should().HaveCount(30);
        carbHalves.Should().HaveCount(30);
    }

    [Fact]
    public async Task SettleAsync_WritesEveryEndAndConfirmationWithOneRead()
    {
        await using var store = await OpenAsync();
        var activations = new ProfileActivationRepository(store.Factory, new SystemAuditContext(), NullLogger<ProfileActivationRepository>.Instance);
        var written = new List<ProfileActivation>();
        for (var i = 0; i < 10; i++)
            written.Add(await activations.CreateAsync(new ProfileActivation { Timestamp = Start.AddHours(i), BaseProfileName = "Day" }, WriteOrigin.Live));
        store.Recorder.Clear();

        await activations.SettleAsync(
            written.ToDictionary(a => a.Id, a => (DateTime?)a.StartTimestamp.AddMinutes(30)),
            written.Skip(1).ToDictionary(a => a.Id, _ => (Guid?)written[0].Id));

        store.Recorder.Count("SELECT").Should().Be(1);
        (await activations.GetOverlappingAsync(Start, Start.AddDays(1), 100))
            .Should().OnlyContain(a => a.EndTimestamp == a.StartTimestamp.AddMinutes(30));
    }

    private static NoteRepository Notes(Store store) =>
        new(store.Factory, Mock.Of<IDeduplicationService>(), new SystemAuditContext(), NullLogger<NoteRepository>.Instance);

    private static Note Note(string legacyId, int minute) => new()
    {
        LegacyId = legacyId, Timestamp = Start.AddMinutes(minute), Text = legacyId,
    };

    private async Task<Store> OpenAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var migrator = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = migrator.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'treatment-batch-reads', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"tbr-{tenant:N}");
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
