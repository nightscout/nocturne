using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// One PostgreSQL container per test process, set up with the production role bootstrap
/// (<c>docs/postgres/container-init/00-init.sh</c>: non-superuser <c>nocturne_migrator</c> and
/// <c>nocturne_app</c>, so FORCE ROW LEVEL SECURITY binds as it does in production), from which
/// each fixture takes a database of its own.
/// </summary>
/// <remarks>
/// <para>
/// Every fixture used to start its own container and run the whole migration chain in it. Here the
/// chain runs once, into a template database, and <see cref="CreateMigratedDatabaseAsync"/> clones
/// it with <c>CREATE DATABASE … TEMPLATE</c>, a file copy. <see cref="CreateEmptyDatabaseAsync"/>
/// clones the bootstrapped but unmigrated database instead, for tests that migrate step by step.
/// </para>
/// <para>
/// A migration test seeds at the migration before the one it tests, most of the way along the
/// chain. <see cref="CreateDatabaseAtMigrationAsync"/> clones the nearest checkpoint at or below
/// that migration and migrates the rest of the way. Checkpoint <c>k</c> holds the first
/// <c>k × </c><see cref="CheckpointInterval"/> migrations and is built once per process, on first
/// use, by cloning checkpoint <c>k − 1</c> and migrating forward. The fully migrated template is
/// built apart from the checkpoints, through the API's own migration path.
/// </para>
/// <para>
/// The container runs with durability off on a tmpfs data directory, like the e2e stack: nothing
/// in it outlives the process. Testcontainers' reaper removes it when the process exits.
/// </para>
/// </remarks>
public static class SharedPostgres
{
    private const string EmptyTemplate = "nocturne_empty";
    private const string MigratedTemplate = "nocturne_migrated";
    private const string SuperPassword = "shared-postgres-test-only";
    private const string MigratorPassword = "shared-migrator-test-only";
    private const string AppPassword = "shared-app-test-only";
    private const string WebPassword = "shared-web-test-only";

    /// <summary>
    /// Migrations between checkpoints. A shorter interval adds a file copy, taken under
    /// <see cref="CloneLock"/>, to the walk up the chain for each checkpoint; a longer one leaves
    /// more of the chain for each request to migrate on its own clone.
    /// </summary>
    private const int CheckpointInterval = 25;

    private static readonly Lazy<string[]> Migrations = new(ReadMigrations, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Task<Server>> Instance = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    // Postgres refuses to copy a template another session is connected to, and CREATE DATABASE
    // itself holds the template, so clones are taken one at a time.
    private static readonly SemaphoreSlim CloneLock = new(1, 1);

    /// <summary>A fresh database holding the full migrated schema and the startup reconcilers' output.</summary>
    public static async Task<TestDatabase> CreateMigratedDatabaseAsync(string prefix)
    {
        var server = await Instance.Value;
        await server.MigratedTemplateReady;
        return await server.CloneAsync(MigratedTemplate, prefix);
    }

    /// <summary>A fresh database with the roles and grants the bootstrap applies, and no schema.</summary>
    public static async Task<TestDatabase> CreateEmptyDatabaseAsync(string prefix)
    {
        var server = await Instance.Value;
        return await server.CloneAsync(EmptyTemplate, prefix);
    }

    /// <summary>
    /// A fresh database migrated up to and including <paramref name="migration"/>, as a test's own
    /// <see cref="IMigrator"/> with <see cref="TenantConnectionInterceptor"/> would leave it, and
    /// without the startup reconcilers' output.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="migration"/> is not in the chain.</exception>
    public static async Task<TestDatabase> CreateDatabaseAtMigrationAsync(string migration, string prefix)
    {
        var applied = Array.IndexOf(Migrations.Value, migration) + 1;
        if (applied == 0)
        {
            throw new ArgumentException($"{migration} is not a migration of {nameof(NocturneDbContext)}.", nameof(migration));
        }

        var server = await Instance.Value;
        var database = await server.CloneAsync(await server.CheckpointAsync(applied / CheckpointInterval), prefix);
        if (applied % CheckpointInterval != 0)
        {
            await MigrateAsync(database.MigratorConnectionString, migration);
        }

        return database;
    }

    private static string[] ReadMigrations()
    {
        using var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(npgsql => npgsql.UseNocturneMigrations())
            .Options);
        return [.. context.Database.GetMigrations()];
    }

    /// <remarks>
    /// The command timeout is the production migrator's (<c>CreateMigratorContext</c>): a checkpoint
    /// every migration fixture depends on should not fail on a loaded machine's 30-second default.
    /// </remarks>
    private static async Task MigrateAsync(string migratorConnectionString, string migration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(migratorConnectionString, npgsql => npgsql
                .CommandTimeout((int)TimeSpan.FromHours(1).TotalSeconds)
                .UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(migration);
    }

    private static async Task<Server> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17.6")
            .WithDatabase(EmptyTemplate)
            .WithUsername("postgres")
            .WithPassword(SuperPassword)
            .WithEnvironment("NOCTURNE_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("NOCTURNE_APP_PASSWORD", AppPassword)
            .WithEnvironment("NOCTURNE_WEB_PASSWORD", WebPassword)
            .WithBindMount(RepositoryFile("docs/postgres/container-init/00-init.sh"), "/docker-entrypoint-initdb.d/00-init.sh")
            .WithTmpfsMount("/var/lib/postgresql/data")
            .WithCommand(
                "-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off",
                "-c", "shared_buffers=64MB", "-c", "max_connections=300", "-c", "jit=off")
            .Build();

        await container.StartAsync();
        var server = new Server(container);
        server.MigratedTemplateReady = server.BuildMigratedTemplateAsync();
        return server;
    }

    internal static string RepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, relativePath)))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException($"Could not locate {relativePath} above {AppContext.BaseDirectory}")
            : Path.Join(dir.FullName, relativePath);
    }

    private sealed class Server(PostgreSqlContainer container)
    {
        private readonly ConcurrentDictionary<int, Lazy<Task<string>>> _checkpoints = new();
        private int _sequence;

        public Task MigratedTemplateReady { get; set; } = Task.CompletedTask;

        private string ConnectionString(string database, string user, string password) =>
            new NpgsqlConnectionStringBuilder
            {
                Host = container.Hostname,
                Port = container.GetMappedPublicPort(5432),
                Database = database,
                Username = user,
                Password = password,
            }.ConnectionString;

        public async Task BuildMigratedTemplateAsync()
        {
            await CloneIntoAsync(EmptyTemplate, MigratedTemplate);
            var migrator = ConnectionString(MigratedTemplate, "nocturne_migrator", MigratorPassword);

            // What the API runs at startup, in the same order.
            await DatabaseInitializationExtensions.RunMigrationsAsync(migrator, NullLogger.Instance, new TenantConnectionInterceptor());
            await DatabaseInitializationExtensions.ReconcileShareRlsPoliciesAsync(migrator, NullLogger.Instance);
            await DatabaseInitializationExtensions.ReconcileTenantTableStorageParametersAsync(migrator, NullLogger.Instance);

            // Pooled connections would keep the template "in use" and fail every clone.
            await using var pooled = new NpgsqlConnection(migrator);
            NpgsqlConnection.ClearPool(pooled);
        }

        /// <returns>The template database holding checkpoint <paramref name="k"/>; checkpoint 0 is the empty one.</returns>
        public Task<string> CheckpointAsync(int k) =>
            k == 0
                ? Task.FromResult(EmptyTemplate)
                : _checkpoints.GetOrAdd(k, n => new(() => BuildCheckpointAsync(n), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        /// <remarks>
        /// Migrates without pooling, so no connection outlives the build to keep the template "in use",
        /// whatever data source EF builds over the connection string.
        /// </remarks>
        private async Task<string> BuildCheckpointAsync(int k)
        {
            var source = await CheckpointAsync(k - 1);
            var name = $"nocturne_checkpoint_{k * CheckpointInterval}";
            await CloneIntoAsync(source, name);

            var migrator = ConnectionString(name, "nocturne_migrator", MigratorPassword) + ";Pooling=false";
            await MigrateAsync(migrator, Migrations.Value[k * CheckpointInterval - 1]);
            return name;
        }

        public async Task<TestDatabase> CloneAsync(string template, string prefix)
        {
            var name = $"{prefix}_{Interlocked.Increment(ref _sequence)}";
            await CloneIntoAsync(template, name);
            return new TestDatabase(
                name,
                ConnectionString(name, "nocturne_migrator", MigratorPassword),
                ConnectionString(name, "nocturne_app", AppPassword),
                ConnectionString(name, "postgres", SuperPassword));
        }

        private async Task CloneIntoAsync(string template, string name)
        {
            await CloneLock.WaitAsync();
            try
            {
                await using var conn = new NpgsqlConnection(ConnectionString("postgres", "postgres", SuperPassword) + ";Pooling=false");
                await conn.OpenAsync();
                // FILE_COPY rather than the default WAL_LOG, which writes every page of the template
                // through the WAL; with fsync off the copy and its checkpoint cost next to nothing.
                // Database-level grants live on the database object, which a template copy does
                // not carry; everything inside it (schema owner, default privileges) it does.
                await using var cmd = new NpgsqlCommand(
                    $"""
                    CREATE DATABASE "{name}" TEMPLATE "{template}" OWNER nocturne_migrator STRATEGY = FILE_COPY;
                    GRANT CONNECT ON DATABASE "{name}" TO nocturne_app;
                    GRANT CONNECT ON DATABASE "{name}" TO nocturne_web;
                    """,
                    conn);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                CloneLock.Release();
            }
        }
    }
}

/// <summary>A database on <see cref="SharedPostgres"/>, reachable as each of the production roles.</summary>
/// <param name="Name">The database name.</param>
/// <param name="MigratorConnectionString">As <c>nocturne_migrator</c>: owns the schema, still bound by FORCE RLS.</param>
/// <param name="AppConnectionString">As <c>nocturne_app</c>: the runtime role.</param>
/// <param name="SuperuserConnectionString">As the bootstrap superuser, for setup a test cannot do through the roles.</param>
public sealed record TestDatabase(
    string Name,
    string MigratorConnectionString,
    string AppConnectionString,
    string SuperuserConnectionString);
