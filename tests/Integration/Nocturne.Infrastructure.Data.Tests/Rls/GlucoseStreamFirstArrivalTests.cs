using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The grouped read of each glucose stream's first arrival, which replay classifies readings by
/// (docs/alerts/engine-semantics.md §8), on PostgreSQL under row level security. It runs with the
/// member history clamp lifted, so a reader clamped to the last 24 hours gets the owner's first
/// arrivals: an outage that began before the clamp's edge and ended in a catch-up inside it is not
/// taken for imported history. The window read stays clamped.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class GlucoseStreamFirstArrivalTests(RlsCompletenessFixture fx) : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private Guid _tenant;
    private readonly DateTime _now = DateTime.UtcNow;
    private readonly Guid _writer = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_clamped_reader_gets_the_owners_first_arrivals_and_only_its_window()
    {
        var onTimeBeforeTheEdge = await SeedAsync(_now.AddHours(-30), _now.AddHours(-30), "xdrip", "phone", null);
        await SeedAsync(_now.AddHours(-40), _now.AddHours(-40), "xdrip", "phone", null, deleted: true);
        var catchUp = await SeedAsync(_now.AddHours(-20), _now.AddHours(-2), "xdrip", "phone", null);
        await SeedAsync(_now.AddHours(-5), _now.AddHours(-4), "app", null, _writer);
        await SeedAsync(_now.AddHours(-6), _now.AddHours(-6), null, null, null);

        var owner = await ReadAsync(clamped: false);
        var clamped = await ReadAsync(clamped: true);

        owner.Arrivals.Should().BeEquivalentTo(
        [
            new GlucoseStreamArrival(null, "xdrip", "phone", _now.AddHours(-30)),
            new GlucoseStreamArrival(_writer, "app", null, _now.AddHours(-4)),
            new GlucoseStreamArrival(null, null, null, _now.AddHours(-6)),
        ], o => o.Using<DateTime>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1))).WhenTypeIs<DateTime>());
        clamped.Arrivals.Should().BeEquivalentTo(owner.Arrivals, "the first arrivals are read with the clamp lifted");

        owner.Window.Should().Contain([onTimeBeforeTheEdge, catchUp]);
        clamped.Window.Should().Contain(catchUp).And.NotContain(onTimeBeforeTheEdge, "the window read stays clamped");
    }

    private async Task<(List<GlucoseStreamArrival> Arrivals, List<Guid> Window)> ReadAsync(bool clamped)
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        if (clamped)
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        var repository = new SensorGlucoseRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            Mock.Of<IDuplicateLinker>(),
            Mock.Of<IAuditContext>(),
            NullLogger<SensorGlucoseRepository>.Instance);
        var arrivals = (await repository.GetStreamFirstArrivalsAsync()).ToList();
        var window = (await repository.GetStreamReadingsAsync(_now.AddDays(-2), _now)).Select(r => r.Id).ToList();
        return (arrivals, window);
    }

    private async Task<Guid> SeedAsync(
        DateTime at, DateTime arrival, string? source, string? device, Guid? writer, bool deleted = false)
    {
        var id = Guid.NewGuid();
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await PlanTestSupport.SetTenantAsync(conn, _tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sensor_glucose
                (id, tenant_id, timestamp, mgdl, data_source, device, writer_grant_id, sys_created_at, sys_updated_at, deleted_at)
            VALUES (@id, @tid, @at, 100, @source, @device, @writer, @arrival, @arrival, @deleted)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", _tenant);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("arrival", arrival);
        cmd.Parameters.Add(new NpgsqlParameter("source", (object?)source ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("device", (object?)device ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("writer", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)writer ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("deleted", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = deleted ? _now : DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }
}
