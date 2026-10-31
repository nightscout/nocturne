using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SourceOwnedDataRepository"/> on PostgreSQL under the runtime role and Row Level
/// Security: a purge and a release run as the scope's tenant reach none of another tenant's rows of
/// the same source.
/// </summary>
[Trait("Category", "Integration")]
public class SourceOwnedDataRepositoryPostgresTests(SourceOwnedDataRepositoryPostgresTests.Database database)
    : IClassFixture<SourceOwnedDataRepositoryPostgresTests.Database>
{
    private const string Source = "loop-connector";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("source_owned_data_repo");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>A scope that reads as <paramref name="scope"/>.</summary>
    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : INocturneDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create(scope));

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private SourceOwnedDataRepository Repository(Guid scope) => new(new Factory(Context, scope), new UserAuditContext());

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'source-owned-data-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"source-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = Context(tenant);
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, LegacyId = $"bolus-{tenant:N}",
            DataSource = Source, Timestamp = DateTime.UtcNow, Insulin = 1,
        });
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Source = Source,
            Category = "PumpMode", State = "Automatic", StartTimestamp = DateTime.UtcNow,
        });
        db.TherapySettings.Add(new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = Source, Timestamp = DateTime.UtcNow,
            ProfileName = "Day",
        });
        await db.SaveChangesAsync();
        return tenant;
    }

    [Fact]
    public async Task APurgeAsTenantADeletesNothingOfTenantB()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        var result = await Repository(a).SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);
        await Repository(a).AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        result.DeletedCounts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SourceDataTypes.Boluses] = 1,
            [SourceDataTypes.StateSpans] = 1,
            [SourceDataTypes.Profiles] = 1,
        });
        (await Repository(a).CountsBySourceAsync(Source, CancellationToken.None)).Should().BeEmpty();
        (await Repository(b).CountsBySourceAsync(Source, CancellationToken.None)).Should().BeEquivalentTo(
            new Dictionary<string, long>
            {
                [SourceDataTypes.Boluses] = 1,
                [SourceDataTypes.StateSpans] = 1,
                [SourceDataTypes.Profiles] = 1,
            });
        await using (var db = Context(b))
            (await db.Boluses.IgnoreQueryFilters().SingleAsync()).DeletedAt.Should().BeNull();
        await using (var db = Context(a))
        {
            var bolus = await db.Boluses.IgnoreQueryFilters().SingleAsync();
            bolus.DeletedAt.Should().NotBeNull();
            db.Entry(bolus).Property<bool>("DeletedByUser").CurrentValue.Should().BeFalse("the release lifts the user tombstone so the source may send the record again");
        }
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/data-sources";
        public bool IsSystem => false;
    }
}
