using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="DeduplicationService.ReconcileNewLinksAsync"/> against PostgreSQL under the runtime
/// role and Row Level Security: two contexts stand in for two instances reconciling one tenant.
/// </summary>
[Trait("Category", "Integration")]
public class DedupReconcileLockTests(DedupReconcileLockTests.Database database)
    : IClassFixture<DedupReconcileLockTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("dedup_reconcile_lock");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Holds each pass after its first read of the cursor until the other pass has read it too, or a
    /// timeout passes. Unserialised passes therefore both read before either merges; a pass holding
    /// the tenant's lock waits out the timeout alone.
    /// </summary>
    private sealed class Rendezvous
    {
        private int _arrived;
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == 2)
                _all.TrySetResult();
            return Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        }
    }

    private sealed class MeetAfterCursorRead(Rendezvous rendezvous) : DbCommandInterceptor
    {
        private int _met;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("dedup_reconcile_state") && Interlocked.Exchange(ref _met, 1) == 0)
                await rendezvous.ArriveAsync();
            return result;
        }
    }

    private NocturneDbContext Context(Guid tenant, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, o => o.EnableRetryOnFailure())
            .AddInterceptors([new TenantConnectionInterceptor(), .. interceptors])
            .Options) { TenantId = tenant };

    private static DeduplicationService Service(NocturneDbContext context) =>
        new(context, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DeduplicationService>.Instance);

    [Fact]
    public async Task Concurrent_passes_over_one_tenant_merge_a_duplicate_pair_once()
    {
        var tenant = await SeedTenantAsync();
        var at = DateTime.UtcNow.AddHours(-1);
        var (first, second) = await SeedDuplicateCarbsAsync(tenant, at);

        var rendezvous = new Rendezvous();
        await using var a = Context(tenant, new MeetAfterCursorRead(rendezvous));
        await using var b = Context(tenant, new MeetAfterCursorRead(rendezvous));

        var results = await Task.WhenAll(
            Task.Run(() => Service(a).ReconcileNewLinksAsync(5000, 4)),
            Task.Run(() => Service(b).ReconcileNewLinksAsync(5000, 4)));

        results.Sum(r => r.GroupsMerged).Should().Be(1);

        await using var check = Context(tenant);
        var links = await check.LinkedRecords.AsNoTracking().ToListAsync();
        links.Select(l => l.RecordId).Should().BeEquivalentTo([first, second]);
        links.Select(l => l.CanonicalId).Distinct().Should().ContainSingle();
        links.Should().ContainSingle(l => l.IsPrimary).Which.RecordId.Should().Be(first);

        var later = await Service(check).ReconcileNewLinksAsync(5000, 4);
        later.Should().Be(new ReconcileResult(0, true), "the lock went with the winning pass's commit");
    }

    private async Task<(Guid First, Guid Second)> SeedDuplicateCarbsAsync(Guid tenant, DateTime at)
    {
        await using var seed = Context(tenant);
        var first = AddCarb(seed, tenant, at, "mylife-connector");
        var second = AddCarb(seed, tenant, at.AddSeconds(20), "glooko-connector");
        await seed.SaveChangesAsync();

        foreach (var link in await seed.LinkedRecords.ToListAsync())
            link.SysCreatedAt = at;
        await seed.SaveChangesAsync();
        return (first, second);
    }

    private static Guid AddCarb(NocturneDbContext context, Guid tenant, DateTime at, string source)
    {
        var id = Guid.CreateVersion7();
        context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = id,
            TenantId = tenant,
            Carbs = 50,
            Timestamp = at,
            DataSource = source,
        });
        context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            CanonicalId = Guid.CreateVersion7(),
            RecordType = "carbintake",
            RecordId = id,
            SourceTimestamp = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            DataSource = source,
            IsPrimary = true,
        });
        return id;
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'dedup-reconcile-lock-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"dedup-lock-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }
}
