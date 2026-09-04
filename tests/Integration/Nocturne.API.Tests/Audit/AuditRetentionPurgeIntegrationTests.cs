using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Audit;

/// <summary>
/// Exercises <see cref="AuditRetentionService"/> against real PostgreSQL Row-Level Security.
/// This is the half the InMemory unit tests cannot cover: <c>mutation_audit_log</c> and
/// <c>read_access_log</c> are <c>FORCE ROW LEVEL SECURITY</c>, so a DELETE issued without
/// <c>app.current_tenant_id</c> on the session matches no rows and returns 0 having succeeded.
/// The InMemory provider has no RLS and the existing unit test substitutes
/// <c>PurgeBatchedAsync</c> outright, so both are blind to that failure.
/// </summary>
[Trait("Category", "Integration")]
public class AuditRetentionPurgeIntegrationTests : AspireIntegrationTestBase
{
    public AuditRetentionPurgeIntegrationTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task PurgeBatchedAsync_DeletesExpiredRows_AndSparesRowsInsideTheWindow()
    {
        var connStr = await GetPostgresConnectionStringAsync()
                      ?? throw new InvalidOperationException("No PostgreSQL connection string.");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = await AuthTestHelpers.SeedTenantAsync(conn, $"audit-purge-{suffix}", "Audit Purge");
        var otherTenant = await AuthTestHelpers.SeedTenantAsync(conn, $"audit-keep-{suffix}", "Audit Keep");

        // A context factory wired exactly like the app: Npgsql + the tenant RLS interceptor.
        await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(dataSource)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        var factory = new InterceptingContextFactory(options);

        var now = DateTime.UtcNow;
        await SeedAuditRowAsync(factory, tenant, now.AddDays(-200));
        await SeedAuditRowAsync(factory, tenant, now.AddDays(-120));
        await SeedAuditRowAsync(factory, tenant, now.AddDays(-10));
        await SeedAuditRowAsync(factory, otherTenant, now.AddDays(-200));

        var service = new AuditRetentionService(
            factory,
            new ConfigurationBuilder().Build(),
            NullLogger<AuditRetentionService>.Instance);

        var deleted = await service.PurgeBatchedAsync(
            tenant, "mutation_audit_log", now.AddDays(-90), CancellationToken.None);

        deleted.Should().Be(2,
            "both rows older than the 90-day cutoff must actually be deleted — an unpinned "
            + "DELETE silently reports 0 under FORCE ROW LEVEL SECURITY");

        (await CountAsync(factory, tenant)).Should().Be(1,
            "the row inside the retention window must survive");
        (await CountAsync(factory, otherTenant)).Should().Be(1,
            "the purge must not reach another tenant's rows");
    }

    private static async Task SeedAuditRowAsync(
        InterceptingContextFactory factory, Guid tenantId, DateTime createdAt)
    {
        await using var ctx = factory.CreateDbContext();
        ctx.TenantId = tenantId; // pins the RLS GUC so the insert's WITH CHECK passes
        ctx.MutationAuditLog.Add(new MutationAuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EntityType = "SensorGlucose",
            EntityId = Guid.CreateVersion7(),
            Action = "update",
            CreatedAt = createdAt,
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<int> CountAsync(InterceptingContextFactory factory, Guid tenantId)
    {
        await using var ctx = factory.CreateDbContext();
        ctx.TenantId = tenantId;
        return await ctx.MutationAuditLog.CountAsync();
    }

    private sealed class InterceptingContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
