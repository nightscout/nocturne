using System.Data.Common;
using Microsoft.Data.Sqlite;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

[Trait("Category", "Unit")]
public class TenantAuditConfigCacheTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public TenantAuditConfigCacheTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using var context = new TestAuditConfigDbContext(_contextOptions);
        context.Database.EnsureCreated();

        // Seed the tenant
        context.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test" });
        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetConfigAsync_WhenNoConfigExists_ReturnsDefaults()
    {
        var factory = new TestDbContextFactory(_contextOptions);
        var cache = new TenantAuditConfigCache(factory);

        var config = await cache.GetConfigAsync(_tenantId);

        config.ReadAuditEnabled.Should().BeFalse();
        config.ReadAuditRetentionDays.Should().BeNull();
        config.MutationAuditRetentionDays.Should().BeNull();
    }

    [Fact]
    public async Task GetConfigAsync_WhenConfigExists_ReturnsConfigValues()
    {
        // Seed a config row
        using (var context = new TestAuditConfigDbContext(_contextOptions))
        {
            context.TenantAuditConfig.Add(new TenantAuditConfigEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                ReadAuditEnabled = true,
                ReadAuditRetentionDays = 90,
                MutationAuditRetentionDays = 365,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        var factory = new TestDbContextFactory(_contextOptions);
        var cache = new TenantAuditConfigCache(factory);

        var config = await cache.GetConfigAsync(_tenantId);

        config.ReadAuditEnabled.Should().BeTrue();
        config.ReadAuditRetentionDays.Should().Be(90);
        config.MutationAuditRetentionDays.Should().Be(365);
    }

    [Fact]
    public async Task GetConfigAsync_SecondCall_ReturnsCachedWithoutDbQuery()
    {
        // Seed a config row
        using (var context = new TestAuditConfigDbContext(_contextOptions))
        {
            context.TenantAuditConfig.Add(new TenantAuditConfigEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                ReadAuditEnabled = true,
                ReadAuditRetentionDays = 30,
                MutationAuditRetentionDays = null,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        var factory = new TrackingDbContextFactory(_contextOptions);
        var cache = new TenantAuditConfigCache(factory);

        // First call — should hit DB
        var config1 = await cache.GetConfigAsync(_tenantId);
        factory.CreateCount.Should().Be(1);

        // Second call — should use cache, no new context created
        var config2 = await cache.GetConfigAsync(_tenantId);
        factory.CreateCount.Should().Be(1);

        config2.Should().Be(config1);
    }

    [Fact]
    public async Task Invalidate_ClearsEntry_NextCallQueriesDb()
    {
        var factory = new TrackingDbContextFactory(_contextOptions);
        var cache = new TenantAuditConfigCache(factory);

        // First call populates cache
        await cache.GetConfigAsync(_tenantId);
        factory.CreateCount.Should().Be(1);

        // Invalidate
        cache.Invalidate(_tenantId);

        // Next call should re-query DB
        await cache.GetConfigAsync(_tenantId);
        factory.CreateCount.Should().Be(2);
    }

    #region Test Helpers

    /// <summary>
    /// Minimal DbContext subclass for tests — removes PostgreSQL-specific query filters
    /// only on the entities we need, avoiding conflicts with owned types.
    /// </summary>
    private class TestAuditConfigDbContext : NocturneDbContext
    {
        public TestAuditConfigDbContext(DbContextOptions<NocturneDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Only clear query filters on non-owned entity types to avoid
            // conflicts with OwnsOne/OwnsMany configurations.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (entityType.IsOwned())
                    continue;

                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter(null as System.Linq.Expressions.LambdaExpression);
            }
        }
    }

    /// <summary>
    /// Simple IDbContextFactory that creates real SQLite-backed contexts.
    /// </summary>
    private class TestDbContextFactory : IDbContextFactory<NocturneDbContext>
    {
        private readonly DbContextOptions<NocturneDbContext> _options;

        public TestDbContextFactory(DbContextOptions<NocturneDbContext> options) => _options = options;

        public NocturneDbContext CreateDbContext() => new TestAuditConfigDbContext(_options);
    }

    /// <summary>
    /// Factory that tracks how many contexts were created, to verify caching behavior.
    /// </summary>
    private class TrackingDbContextFactory : IDbContextFactory<NocturneDbContext>
    {
        private readonly DbContextOptions<NocturneDbContext> _options;

        public TrackingDbContextFactory(DbContextOptions<NocturneDbContext> options) => _options = options;

        public int CreateCount { get; private set; }

        public NocturneDbContext CreateDbContext()
        {
            CreateCount++;
            return new TestAuditConfigDbContext(_options);
        }
    }

    #endregion
}
