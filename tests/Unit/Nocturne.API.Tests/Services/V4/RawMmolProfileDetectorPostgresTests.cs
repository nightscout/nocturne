using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// <see cref="RawMmolProfileDetector"/> on PostgreSQL under the runtime role, over stores written
/// directly. The migration's own cases run in the API integration project.
/// </summary>
[Trait("Category", "Integration")]
public class RawMmolProfileDetectorPostgresTests(RawMmolProfileDetectorPostgresTests.Database database)
    : IClassFixture<RawMmolProfileDetectorPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("raw_mmol_detector");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class Contexts(string connectionString, Guid tenant) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(new TenantConnectionInterceptor())
                .Options) { TenantId = tenant };
    }

    private sealed record Store(Guid TargetId, Guid SensitivityId);

    private static readonly DateTime Uploaded = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private async Task<(INocturneDbContextFactory Factory, Guid Tenant)> TenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'raw-mmol-detector-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"raw-mmol-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        return (new Contexts(database.AppConnectionString, tenant).AsNocturneFactory(tenant), tenant);
    }

    private static async Task<Store> SeedAsync(
        INocturneDbContextFactory factory, Guid tenant, string profile, string units, string targets, string isf)
    {
        var store = new Store(Guid.CreateVersion7(), Guid.CreateVersion7());
        var legacyId = $"doc:{profile}";
        await using var db = await factory.CreateAsync();
        db.TherapySettings.Add(new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = Uploaded, LegacyId = legacyId,
            ProfileName = profile, Units = units,
        });
        db.TargetRangeSchedules.Add(new TargetRangeScheduleEntity
        {
            Id = store.TargetId, TenantId = tenant, Timestamp = Uploaded, LegacyId = legacyId,
            ProfileName = profile, EntriesJson = targets,
        });
        db.SensitivitySchedules.Add(new SensitivityScheduleEntity
        {
            Id = store.SensitivityId, TenantId = tenant, Timestamp = Uploaded, LegacyId = legacyId,
            ProfileName = profile, EntriesJson = isf,
        });
        await db.SaveChangesAsync();
        return store;
    }

    private static Task<Store> MmolStoreAsync(INocturneDbContextFactory factory, Guid tenant) =>
        SeedAsync(factory, tenant, "Mmol", "mmol",
            """[{"Time":"00:00","Low":5.0,"High":7.5},{"Time":"06:00","Low":"x","High":8}]""",
            """[{"Time":"00:00","Value":3.0},{"Value":2.5},"not an entry"]""");

    [Fact]
    public async Task AMmolStoreWithRawValues_ListsItsTargetsAndItsIsf()
    {
        var (factory, tenant) = await TenantAsync();
        var store = await MmolStoreAsync(factory, tenant);

        var listed = await new RawMmolProfileDetector(factory).DetectAsync();

        var target = listed.Should().ContainSingle(i => i.Id == store.TargetId).Subject;
        target.Kind.Should().Be(RawMmolScheduleKind.TargetRange);
        target.Reason.Should().Be(RawMmolReason.TargetInMmolBand);
        target.CanDismiss.Should().BeFalse();
        target.LegacyId.Should().Be("doc:Mmol");
        target.Entries.Select(e => (e.Time, e.Low, e.High))
            .Should().Equal(new (string?, double?, double?)[] { ("00:00", 5.0, 7.5), ("06:00", null, 8.0) });

        var isf = listed.Should().ContainSingle(i => i.Id == store.SensitivityId).Subject;
        isf.Kind.Should().Be(RawMmolScheduleKind.Sensitivity);
        isf.Reason.Should().Be(RawMmolReason.IsfMayBeMmol);
        isf.CanDismiss.Should().BeTrue();
        isf.Entries.Select(e => (e.Time, e.Value))
            .Should().Equal(new (string?, double?)[] { ("00:00", 3.0), (null, 2.5) });
    }

    [Fact]
    public async Task AMgdlStore_IsNotListed()
    {
        var (factory, tenant) = await TenantAsync();
        await SeedAsync(factory, tenant, "Mgdl", "mg/dl",
            """[{"Time":"00:00","Low":90,"High":140}]""", """[{"Time":"00:00","Value":40}]""");

        var listed = await new RawMmolProfileDetector(factory).DetectAsync();

        listed.Should().BeEmpty();
    }

    [Fact]
    public async Task DismissingAnIsf_UnlistsItAndLeavesItsTargets()
    {
        var (factory, tenant) = await TenantAsync();
        var store = await MmolStoreAsync(factory, tenant);
        var detector = new RawMmolProfileDetector(factory);

        (await detector.DismissIsfAsync(store.SensitivityId)).Should().BeTrue();

        var listed = await detector.DetectAsync();
        listed.Should().NotContain(i => i.Id == store.SensitivityId);
        listed.Should().ContainSingle(i => i.Id == store.TargetId);
    }

    [Fact]
    public async Task DismissingAnUnknownIsf_ReturnsFalse()
    {
        var (factory, _) = await TenantAsync();

        (await new RawMmolProfileDetector(factory).DismissIsfAsync(Guid.CreateVersion7())).Should().BeFalse();
    }

    [Fact]
    public async Task DismissingAnIsfWithNoTherapySettings_ReturnsFalse()
    {
        var (factory, tenant) = await TenantAsync();
        var isfId = Guid.CreateVersion7();
        await using (var db = await factory.CreateAsync())
        {
            db.SensitivitySchedules.Add(new SensitivityScheduleEntity
            {
                Id = isfId, TenantId = tenant, Timestamp = Uploaded, LegacyId = "doc:Orphan",
                ProfileName = "Orphan", EntriesJson = """[{"Time":"00:00","Value":3.0}]""",
            });
            await db.SaveChangesAsync();
        }

        (await new RawMmolProfileDetector(factory).DismissIsfAsync(isfId)).Should().BeFalse();
    }
}
