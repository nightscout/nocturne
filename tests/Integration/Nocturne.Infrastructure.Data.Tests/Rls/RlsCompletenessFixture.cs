using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Security;
using Npgsql;
using Testcontainers.PostgreSql;
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
    private const string DbName = "nocturne_rls_completeness";
    private const string BootstrapUser = "postgres";
    private const string BootstrapPassword = "bootstrap-test-password";
    private const string MigratorPassword = "rls-completeness-migrator-password";
    private const string AppPassword = "rls-completeness-app-password";
    private const string WebPassword = "rls-completeness-web-password";

    private PostgreSqlContainer? _container;

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
        var initScriptPath = ResolveInitScriptPath();

        _container = new PostgreSqlBuilder("postgres:17.6")
            .WithDatabase(DbName)
            .WithUsername(BootstrapUser)
            .WithPassword(BootstrapPassword)
            .WithEnvironment("NOCTURNE_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("NOCTURNE_APP_PASSWORD", AppPassword)
            .WithEnvironment("NOCTURNE_WEB_PASSWORD", WebPassword)
            .WithBindMount(initScriptPath, "/docker-entrypoint-initdb.d/00-init.sh")
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(5432);

        MigratorConnectionString =
            $"Host={host};Port={port};Database={DbName};Username=nocturne_migrator;Password={MigratorPassword}";
        AppConnectionString =
            $"Host={host};Port={port};Database={DbName};Username=nocturne_app;Password={AppPassword}";

        await DatabaseInitializationExtensions.RunMigrationsAsync(
            MigratorConnectionString,
            NullLogger.Instance,
            new TenantConnectionInterceptor());

        // Apply the per-category public-share RLS policies, exactly as the API does at startup.
        await DatabaseInitializationExtensions.ReconcileShareRlsPoliciesAsync(
            MigratorConnectionString,
            NullLogger.Instance);

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

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

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

    private static string ResolveInitScriptPath()
    {
        // Walk up from the test assembly's base directory until we find the
        // canonical init script. Tests can run from various working dirs
        // (dotnet test, IDE, CI runner), so a hardcoded relative path is fragile.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "docs/postgres/container-init/00-init.sh")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate docs/postgres/container-init/00-init.sh by walking up from " + AppContext.BaseDirectory);
        }

        return Path.Join(dir.FullName, "docs/postgres/container-init/00-init.sh");
    }
}

[CollectionDefinition("RLS completeness")]
public class RlsCompletenessCollection : ICollectionFixture<RlsCompletenessFixture>
{
}
