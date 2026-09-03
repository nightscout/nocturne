using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Storage.Internal;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Tests.Shared.Infrastructure;

public static class TestDbContextFactory
{
    /// <param name="interceptors">Interceptors the behaviour under test depends on, e.g.
    /// <c>MutationAuditInterceptor</c> for anything reading the soft-delete attribution flag.</param>
    public static NocturneDbContext CreateInMemoryContext(
        string? databaseName = null, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"nocturne_tests_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptors)
            .EnableSensitiveDataLogging()
            .Options;

        var context = new NocturneDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>
    /// Creates an empty SQLite-backed schema. Use when the test seeds its own tenant rows,
    /// for example because it needs several tenants or a non-default slug.
    /// </summary>
    public static SqliteTestDatabase CreateSqlite() => new(tenantId: null, tenantSlug: null);

    /// <summary>
    /// Creates a SQLite-backed schema with one tenant row, so inserts that carry a
    /// <c>tenant_id</c> foreign key succeed.
    /// </summary>
    /// <param name="interceptors">Interceptors the behaviour under test depends on, e.g.
    /// <c>MutationAuditInterceptor</c> for anything reading the soft-delete attribution flag.</param>
    public static SqliteTestDatabase CreateSqliteWithTenant(
        Guid tenantId, string tenantSlug = "test", params IInterceptor[] interceptors) =>
        new(tenantId, tenantSlug, interceptors);
}

/// <summary>
/// An open SQLite in-memory connection and the <see cref="NocturneDbContext"/> options bound
/// to it. The schema only lives as long as the connection, so the owning fixture must dispose
/// this after the test.
/// </summary>
public sealed class SqliteTestDatabase : IDisposable
{
    internal SqliteTestDatabase(Guid? tenantId, string? tenantSlug, params IInterceptor[] interceptors)
    {
        TenantId = tenantId ?? Guid.Empty;

        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();

        Options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(Connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(interceptors)
            .EnableSensitiveDataLogging()
            .Options;

        ContextFactory = new PooledContextFactory(this);

        using var seed = CreateContext();
        seed.Database.EnsureCreated();

        if (tenantSlug is null)
        {
            return;
        }

        seed.Tenants.Add(new TenantEntity { Id = TenantId, Slug = tenantSlug });
        seed.SaveChanges();
    }

    public SqliteConnection Connection { get; }

    public DbContextOptions<NocturneDbContext> Options { get; }

    public Guid TenantId { get; }

    /// <summary>
    /// Stands in for the registered context pool, for services that take
    /// <see cref="IDbContextFactory{TContext}"/> rather than an injected context. Every call hands
    /// out a fresh context over the same connection, pinned to <see cref="TenantId"/>.
    /// </summary>
    public IDbContextFactory<NocturneDbContext> ContextFactory { get; }

    public NocturneDbContext CreateContext() => CreateContext(TenantId);

    public NocturneDbContext CreateContext(Guid tenantId) => new(Options) { TenantId = tenantId };

    /// <summary>Adds a further tenant row, for tests that assert one tenant cannot reach another's.</summary>
    public SqliteTestDatabase SeedTenant(Guid tenantId, string slug)
    {
        using var db = CreateContext();
        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = slug });
        db.SaveChanges();
        return this;
    }

    public void Dispose() => Connection.Dispose();

    private sealed class PooledContextFactory(SqliteTestDatabase db)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => db.CreateContext();

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}
