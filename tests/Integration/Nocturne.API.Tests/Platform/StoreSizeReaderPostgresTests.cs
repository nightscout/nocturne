using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Integration.Tests.Platform;

[Trait("Category", "Integration")]
public sealed class StoreSizeReaderPostgresTests(StoreSizePostgresFixture fixture)
    : IClassFixture<StoreSizePostgresFixture>
{
    private const int LargeRows = StoreSizePostgresFixture.LargeRows;
    private const int SmallRows = StoreSizePostgresFixture.SmallRows;

    private ServiceProvider _provider => fixture.Provider;
    private Guid _large => fixture.Large;
    private Guid _small => fixture.Small;

    [Fact]
    public async Task EachTenantsEstimateReflectsItsOwnRows_AndNeitherIsTheDatabaseTotal()
    {
        await using var scope = _provider.CreateAsyncScope();
        var reader = ReaderIn(scope, out _);

        var large = await reader.EstimateAsync(_large);
        var small = await reader.EstimateAsync(_small);
        var database = await reader.ReadDatabaseSizeAsync();

        (large.DataSize + large.IndexSize).Should().BeGreaterThan(0);
        (small.DataSize + small.IndexSize).Should().BeGreaterThan(0);
        (large.DataSize + large.IndexSize).Should().BeGreaterThan(small.DataSize + small.IndexSize);
        (large.DataSize + large.IndexSize).Should().BeLessThan(database.TotalBytes);
        (large.DataSize + large.IndexSize + small.DataSize + small.IndexSize).Should().BeLessThan(database.TotalBytes);

        var stepCounts = await fixture.RelationBytesAsync("step_counts");
        var share = (double)(LargeRows - SmallRows) / (LargeRows + SmallRows);
        ((large.DataSize + large.IndexSize) - (small.DataSize + small.IndexSize))
            .Should().BeCloseTo((long)(stepCounts * share), 2048,
                "the tenants differ only in step_counts, by the difference of their shares of it");

        large.Collections.Should().BeGreaterThan(50);
        large.Indexes.Should().BeGreaterThanOrEqualTo(large.Collections);
    }

    [Fact]
    public async Task ATableWithNoUsableRowEstimate_IsAttributedWholeToTheTenant()
    {
        await using var scope = _provider.CreateAsyncScope();
        var reader = ReaderIn(scope, out _);
        var stepCounts = await fixture.RelationBytesAsync("step_counts");
        var before = await reader.EstimateAsync(_small);

        await fixture.ExecuteAsync("UPDATE pg_class SET reltuples = -1 WHERE relname = 'step_counts'");
        DbStats after;
        try
        {
            after = await reader.EstimateAsync(_small);
        }
        finally
        {
            await fixture.ExecuteAsync("ANALYZE step_counts");
        }

        var proportional = (long)(stepCounts * ((double)SmallRows / (LargeRows + SmallRows)));
        ((after.DataSize + after.IndexSize) - (before.DataSize + before.IndexSize))
            .Should().BeCloseTo(stepCounts - proportional, 2048);
    }

    [Fact]
    public async Task ReadAsync_EstimatesTheScopesTenant_ThroughTheCache()
    {
        await using var scope = _provider.CreateAsyncScope();
        SetTenant(scope, _large);
        var reader = ReaderIn(scope, out var cache);

        var viaCache = await reader.ReadAsync();

        viaCache.Should().BeEquivalentTo(await reader.EstimateAsync(_large));
        cache.Verify(
            x => x.GetOrSetAsync(It.IsAny<string>(), It.IsAny<Func<Task<DbStats>>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AHistoryClampedReader_GetsZero_AndLeavesTheCacheUntouched()
    {
        await using var scope = _provider.CreateAsyncScope();
        SetTenant(scope, _large);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        var reader = ReaderIn(scope, out var cache);

        (await reader.ReadAsync()).Should().BeEquivalentTo(new DbStats());
        cache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APublicShare_GetsZero_AndLeavesTheCacheUntouched(bool fullHistory)
    {
        await using var scope = _provider.CreateAsyncScope();
        SetTenant(scope, _large);
        var read = scope.ServiceProvider.GetRequiredService<ICategoryReadContext>();
        read.MarkShare();
        read.SetVisibleCategories("stepcount.read");
        read.SetFullHistory(fullHistory);
        var reader = ReaderIn(scope, out var cache);

        (await reader.ReadAsync()).Should().BeEquivalentTo(new DbStats());
        cache.VerifyNoOtherCalls();
    }

    private static void SetTenant(AsyncServiceScope scope, Guid tenant) =>
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
            .SetTenant(new TenantContext(tenant, "t", "T", true, false));

    private StoreSizeReader ReaderIn(AsyncServiceScope scope, out Mock<ITenantCache> cache)
    {
        var services = scope.ServiceProvider;
        cache = new Mock<ITenantCache>();
        cache
            .Setup(x => x.GetOrSetAsync(It.IsAny<string>(), It.IsAny<Func<Task<DbStats>>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Func<Task<DbStats>> factory, TimeSpan _, CancellationToken _) => factory());

        return new StoreSizeReader(
            services.GetRequiredService<INocturneDbContextFactory>(),
            cache.Object,
            services.GetRequiredService<ICategoryReadContext>(),
            services.GetRequiredService<ITenantAccessor>(),
            services.GetRequiredService<IReaderCachePartition>(),
            NullLogger<StoreSizeReader>.Instance);
    }
}

public sealed class StoreSizePostgresFixture : IAsyncLifetime
{
    public const int LargeRows = 4000;
    public const int SmallRows = 1000;

    private TestDatabase _database = null!;

    public ServiceProvider Provider { get; private set; } = null!;
    public Guid Large { get; private set; }
    public Guid Small { get; private set; }

    public async Task InitializeAsync()
    {
        _database = await SharedPostgres.CreateMigratedDatabaseAsync("store_size");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlInfrastructure(_database.AppConnectionString, configuration: null);
        services.AddScoped<ITenantAccessor, SettableTenantAccessor>();
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Large = await SeedTenantAsync("large", LargeRows);
        Small = await SeedTenantAsync("small", SmallRows);
        // Autovacuum's first pass would otherwise add a visibility map page between a test's reads.
        await ExecuteAsync("VACUUM ANALYZE");
    }

    public async Task DisposeAsync() => await Provider.DisposeAsync();

    private async Task<Guid> SeedTenantAsync(string name, int rows)
    {
        var tenant = Guid.NewGuid();
        await ExecuteAsync(
            $"INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES ('{tenant}', 'size-{name}-{tenant:N}', '{name}', true, now(), now())");

        var now = DateTime.UtcNow;
        await using var db = await Provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        db.StepCounts.AddRange(Enumerable.Range(0, rows).Select(i =>
            new StepCountEntity { Id = Guid.NewGuid(), Timestamp = now.AddMinutes(-i) }));
        await db.SaveChangesAsync();
        return tenant;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_database.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> RelationBytesAsync(string table)
    {
        await using var conn = new NpgsqlConnection(_database.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT pg_total_relation_size('{table}')";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed class SettableTenantAccessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public TenantContext? Context { get; private set; }
        public void SetTenant(TenantContext context) => Context = context;
    }
}
