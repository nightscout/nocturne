using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Shared xUnit fixture for RLS completeness tests. Spins up a PostgreSQL
/// container with the canonical docs/postgres/container-init/00-init.sh
/// bind-mounted so the nocturne_migrator + nocturne_app roles exist before
/// migrations run. Runs full EF Core migrations under the migrator role and
/// then exposes connection strings for both roles.
///
/// Deliberately seedless: the completeness, canonical-fingerprint, and
/// negative tests inspect schema metadata (pg_class, pg_policy) and don't
/// need row data. Keeping the fixture seedless makes it immune to future
/// schema drift on the seeded tables.
/// </summary>
public class RlsCompletenessFixture : IAsyncLifetime
{
    public string AppConnectionString { get; private set; } = string.Empty;
    public string MigratorConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Tables the suites in this collection expect the live database to have policied, cascaded
    /// and forced. Deliberately NOT <see cref="ShareRlsPolicy.TenantScopedTableNames"/>: that
    /// method produced the DDL under test, so asserting against it would only restate what the
    /// reconciler was told to do. Walks <see cref="ITenantScoped"/> CLR types instead and asks
    /// the model for each one's table, so a table dropped from the reconciler's set still
    /// appears here and the database is caught missing it.
    /// </summary>
    public IReadOnlyList<string> TenantScopedTableNames { get; private set; } = [];

    public async Task InitializeAsync()
    {
        // A clone of the shared migrated template: migrations, the per-category share RLS policies
        // and the tenant-table storage parameters, exactly as the API applies them at startup.
        var database = await SharedPostgres.CreateMigratedDatabaseAsync("rls_completeness");
        MigratorConnectionString = database.MigratorConnectionString;
        AppConnectionString = database.AppConnectionString;

        await using var context = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(MigratorConnectionString).Options);

        TenantScopedTableNames = typeof(ITenantScoped).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantScoped).IsAssignableFrom(t))
            .Select(t => context.Model.FindEntityType(t)?.GetTableName())
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task<NpgsqlConnection> OpenAppConnectionAsync()
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<NpgsqlConnection> OpenMigratorConnectionAsync()
    {
        var conn = new NpgsqlConnection(MigratorConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

[CollectionDefinition("RLS completeness")]
public class RlsCompletenessCollection : ICollectionFixture<RlsCompletenessFixture>
{
}
