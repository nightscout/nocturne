using Microsoft.Extensions.Logging.Abstractions;
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
