using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The baseline stamp of a reconcile batch against PostgreSQL as <c>nocturne_app</c>: each of the
/// source's unfingerprinted rows takes its own id's fingerprint, in one statement per table.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class DecomposedTreatmentKeysStampTests(RlsCompletenessFixture fx)
{
    private const string Connector = "nightscout-connector";
    private const string Other = "other-connector";
    private static readonly DateTime At = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ABatchStampsEachOfTheSourcesUnfingerprintedRowsWithItsOwnFingerprint_InOneStatementPerTable()
    {
        await using var store = await OpenAsync();
        string[] ids = [.. Enumerable.Range(0, 12).Select(i => $"t-{i}")];
        foreach (var id in ids)
            store.Context.Boluses.Add(Bolus(store.Tenant, id));
        store.Context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = store.Tenant, Timestamp = At, Carbs = 10, DataSource = Connector,
            LegacyId = "t-0", UpstreamFingerprint = "old",
        });
        store.Context.Notes.Add(new NoteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = store.Tenant, Timestamp = At, Text = "note", DataSource = Other,
            LegacyId = "t-1",
        });
        store.Context.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = store.Tenant, Category = nameof(StateSpanCategory.Override),
            State = "Custom", StartTimestamp = At, Source = Connector, LegacyId = "t-2",
        });
        await store.Context.SaveChangesAsync();
        store.Context.ChangeTracker.Clear();
        store.Recorder.Clear();

        await new DecomposedTreatmentKeys(store.Factory, TimeProvider.System)
            .StampUnfingerprintedAsync(Connector, ids.ToDictionary(id => id, id => $"fp-{id}"));

        store.Recorder.Count("UPDATE").Should().Be(DecomposedTreatmentTables.All.Count + 1);
        (await store.Context.Boluses.AsNoTracking().ToDictionaryAsync(b => b.LegacyId!, b => b.UpstreamFingerprint))
            .Should().BeEquivalentTo(ids.ToDictionary(id => id, id => (string?)$"fp-{id}"));
        (await store.Context.Notes.AsNoTracking().SingleAsync()).UpstreamFingerprint
            .Should().BeNull("another source's row is not this source's to stamp");
        (await store.Context.CarbIntakes.AsNoTracking().SingleAsync()).UpstreamFingerprint.Should().Be("old");
        (await store.Context.StateSpans.AsNoTracking().SingleAsync()).UpstreamFingerprint.Should().Be("fp-t-2");
    }

    private static BolusEntity Bolus(Guid tenant, string legacyId) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = At, Insulin = 1, DataSource = Connector,
        LegacyId = legacyId,
    };

    private async Task<Store> OpenAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var migrator = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = migrator.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'decomposed-treatment-stamp', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"dts-{tenant:N}");
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
        return new Store(conn, context, recorder, tenant);
    }

    private sealed class Store(NpgsqlConnection connection, NocturneDbContext context, StatementRecorder recorder, Guid tenant)
        : IAsyncDisposable
    {
        public Guid Tenant { get; } = tenant;
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
