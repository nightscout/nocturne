using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Extension methods for database initialization: migrations, RLS verification,
/// and runtime configuration checks.
/// </summary>
public static class DatabaseInitializationExtensions
{
    /// <summary>
    /// Runs EF Core migrations against the database using a dedicated migrator
    /// connection string. Builds a throwaway NpgsqlDataSource + DbContextOptions
    /// so the migrator DbContext is never registered in the main DI container
    /// and cannot be accidentally resolved at request time.
    ///
    /// The runtime connection interceptor is attached so the role-attribute
    /// guard runs on the migrator connection too.
    /// </summary>
    public static async Task RunMigrationsAsync(
        string migratorConnectionString,
        ILogger logger,
        TenantConnectionInterceptor interceptor,
        CancellationToken cancellationToken = default)
    {
        NpgsqlDataSource? dataSource = null;
        try
        {
            logger.LogInformation("Running PostgreSQL database migrations under migrator role...");

            dataSource = new NpgsqlDataSourceBuilder(migratorConnectionString).Build();

            var optionsBuilder = new DbContextOptionsBuilder<NocturneDbContext>();
            optionsBuilder.UseNpgsql(dataSource);
            optionsBuilder.AddInterceptors(interceptor);

            using var context = new NocturneDbContext(optionsBuilder.Options);
            await context.Database.MigrateAsync(cancellationToken);

            logger.LogInformation("PostgreSQL database migrations completed");
        }
        catch (PostgresException ex) when (
            ex.SqlState is "28000" or "28P01" ||
            (ex.Message.Contains("role \"") && ex.Message.Contains("does not exist")))
        {
            var csb = new NpgsqlConnectionStringBuilder(migratorConnectionString);
            throw new InvalidOperationException(
                $"""
                The PostgreSQL role '{csb.Username}' does not exist in database '{csb.Database}'.
                Nocturne requires two separate non-privileged roles: nocturne_migrator and
                nocturne_app. Create them by running docs/postgres/bootstrap-roles.sql
                against your database as a superuser, then restart Nocturne.

                For Aspire and self-hosted docker-compose deployments this is done
                automatically via the Postgres container's init script. For bring-your-own
                PostgreSQL deployments (managed PostgreSQL, existing shared instances) you
                must run bootstrap-roles.sql once per database.
                """, ex);
        }
        catch (PostgresException ex) when (ex.SqlState is "3D000")
        {
            var csb = new NpgsqlConnectionStringBuilder(migratorConnectionString);
            throw new InvalidOperationException(
                $"Database '{csb.Database}' does not exist. Create it as a superuser " +
                "(`CREATE DATABASE ...`), then run docs/postgres/bootstrap-roles.sql against it.",
                ex);
        }
        finally
        {
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Validates runtime database configuration after migrations have run and
    /// the app DbContext is registered. Runs the RLS self-check under the app
    /// role and asserts the runtime NpgsqlDataSource is configured with
    /// NoResetOnClose = false (required so pooled connections DISCARD ALL
    /// between uses, wiping app.current_tenant_id).
    /// </summary>
    public static async Task ValidateDatabaseConfigurationAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<NocturneDbContext>>();

        var tenantScopedTables = context.Model.GetEntityTypes()
            .Where(et => typeof(ITenantScoped).IsAssignableFrom(et.ClrType))
            .Select(et => et.GetTableName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct()
            .ToArray();

        await VerifyRlsAsync(
            context.Database.GetDbConnection(),
            tenantScopedTables,
            logger,
            cancellationToken);

        VerifyNoResetOnClose(context, logger);
    }

    /// <summary>
    /// Validates that the migrator and app connection strings reference
    /// different PostgreSQL users. Throws on startup if they're the same --
    /// that defeats the entire role separation model. Call this BEFORE
    /// RunMigrationsAsync during Program.cs startup.
    /// </summary>
    public static void ValidateRoleSeparation(
        string appConnectionString,
        string migratorConnectionString)
    {
        var appCsb = new NpgsqlConnectionStringBuilder(appConnectionString);
        var migratorCsb = new NpgsqlConnectionStringBuilder(migratorConnectionString);

        if (string.Equals(appCsb.Username, migratorCsb.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:NocturneDb and ConnectionStrings:NocturneDbMigrator must use " +
                $"different PostgreSQL users. Both are configured to use '{appCsb.Username}'.");
        }
    }

    /// <summary>
    /// Verifies that every supplied tenant-scoped table has Row Level Security
    /// enabled, forced, and at least one policy. Also checks table ownership and
    /// default privileges, and warns if the connected database user is a superuser
    /// or has BYPASSRLS.
    ///
    /// Runs at API startup after migrations so accidentally adding a new
    /// tenant-scoped table without an accompanying RLS migration fails loud
    /// instead of silently leaking PHI across tenants. Also called directly by
    /// the RLS migration smoke test against a freshly-migrated test database.
    ///
    /// PostgreSQL only -- queries pg_catalog views (pg_class, pg_policy,
    /// pg_namespace, pg_tables, pg_default_acl, pg_roles), so the supplied
    /// connection must be an NpgsqlConnection.
    ///
    /// Connection lifecycle is owned by the caller: the connection is opened
    /// if it isn't already, and is left open on return.
    /// </summary>
    /// <param name="connection">Open or closed DbConnection to run the checks against. Opened if needed and left open.</param>
    /// <param name="tenantScopedTables">Names of tables expected to have RLS configured (typically derived from the EF model walk for <see cref="ITenantScoped"/> entities).</param>
    /// <param name="logger">Logger for pass/warn/fail messages. Pass <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance"/> to suppress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task VerifyRlsAsync(
        DbConnection connection,
        IEnumerable<string> tenantScopedTables,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var tables = tenantScopedTables.ToArray();
        if (tables.Length == 0)
        {
            return;
        }

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        // pg_class.relrowsecurity = ENABLE ROW LEVEL SECURITY
        // pg_class.relforcerowsecurity = FORCE ROW LEVEL SECURITY (applies to table owner too)
        // A table without a policy silently rejects all rows instead of filtering,
        // which is safer but still a bug worth catching.
        const string sql = """
            SELECT c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   (SELECT COUNT(*) FROM pg_policy p WHERE p.polrelid = c.oid) AS policy_count
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relkind = 'r'
              AND c.relname = ANY(@tables);
            """;

        var rows = new List<(string Table, bool RlsEnabled, bool RlsForced, long PolicyCount)>();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = sql;
            var param = cmd.CreateParameter();
            param.ParameterName = "@tables";
            param.Value = tables;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetInt64(3)));
            }
        }

        var foundTables = rows.Select(r => r.Table).ToHashSet(StringComparer.Ordinal);
        var missing = tables.Where(t => !foundTables.Contains(t)).ToArray();
        var notEnabled = rows.Where(r => !r.RlsEnabled).Select(r => r.Table).ToArray();
        var notForced = rows.Where(r => r.RlsEnabled && !r.RlsForced).Select(r => r.Table).ToArray();
        var noPolicy = rows.Where(r => r.RlsEnabled && r.PolicyCount == 0).Select(r => r.Table).ToArray();

        var problems = new List<string>();
        if (missing.Length > 0)
        {
            problems.Add($"tables not found in database: {string.Join(", ", missing)}");
        }
        if (notEnabled.Length > 0)
        {
            problems.Add($"RLS not enabled on: {string.Join(", ", notEnabled)}");
        }
        if (notForced.Length > 0)
        {
            problems.Add($"FORCE ROW LEVEL SECURITY not set on: {string.Join(", ", notForced)}");
        }
        if (noPolicy.Length > 0)
        {
            problems.Add($"no policy defined on: {string.Join(", ", noPolicy)}");
        }

        if (problems.Count > 0)
        {
            var message =
                "Row Level Security self-check failed. Tenant-scoped tables must have RLS enabled, forced, and at least one policy. " +
                string.Join("; ", problems) +
                ". Add a migration that runs ENABLE + FORCE ROW LEVEL SECURITY and creates a tenant_isolation policy.";
            logger.LogCritical("{Message}", message);
            throw new InvalidOperationException(message);
        }

        // Owner check: all tenant-scoped tables should be owned by nocturne_migrator.
        await using (var ownerCmd = connection.CreateCommand())
        {
            ownerCmd.CommandText =
                "SELECT tablename, tableowner FROM pg_tables WHERE schemaname = 'public' AND tablename = ANY(@tables) AND tableowner != 'nocturne_migrator'";
            var ownerParam = ownerCmd.CreateParameter();
            ownerParam.ParameterName = "@tables";
            ownerParam.Value = tables;
            ownerCmd.Parameters.Add(ownerParam);

            var badOwners = new List<string>();
            await using var ownerReader = await ownerCmd.ExecuteReaderAsync(cancellationToken);
            while (await ownerReader.ReadAsync(cancellationToken))
            {
                badOwners.Add($"{ownerReader.GetString(0)} (owner: {ownerReader.GetString(1)})");
            }

            if (badOwners.Count > 0)
            {
                var message =
                    "Table ownership check failed. Tenant-scoped tables must be owned by 'nocturne_migrator'. " +
                    $"Misowned tables: {string.Join(", ", badOwners)}. " +
                    "Re-run docs/postgres/bootstrap-roles.sql or the 00-init.sh container init script.";
                logger.LogCritical("{Message}", message);
                throw new InvalidOperationException(message);
            }
        }

        // Default privileges check: nocturne_migrator must have ALTER DEFAULT PRIVILEGES configured.
        await using (var defAclCmd = connection.CreateCommand())
        {
            defAclCmd.CommandText = """
                SELECT 1 FROM pg_default_acl d
                JOIN pg_roles r ON d.defaclrole = r.oid
                WHERE r.rolname = 'nocturne_migrator'
                  AND d.defaclnamespace = (SELECT oid FROM pg_namespace WHERE nspname = 'public')
                """;
            var result = await defAclCmd.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                const string message =
                    "ALTER DEFAULT PRIVILEGES FOR ROLE nocturne_migrator IN SCHEMA public is not configured. " +
                    "Re-run docs/postgres/bootstrap-roles.sql or the 00-init.sh container init script.";
                logger.LogCritical("{Message}", message);
                throw new InvalidOperationException(message);
            }
        }

        // Secondary check: if the connected role bypasses RLS, all of the above
        // is cosmetic. This is the single most common silent failure mode -- in
        // dev the app typically connects as the Postgres bootstrap superuser.
        await using (var roleCmd = connection.CreateCommand())
        {
            roleCmd.CommandText =
                "SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";
            await using var reader = await roleCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var user = reader.GetString(0);
                var isSuper = reader.GetBoolean(1);
                var bypassRls = reader.GetBoolean(2);

                if (isSuper || bypassRls)
                {
                    logger.LogWarning(
                        "Database user '{User}' bypasses Row Level Security (superuser={IsSuper}, bypassrls={BypassRls}). " +
                        "Tenant isolation is NOT enforced at runtime. Switch the runtime connection to the non-privileged " +
                        "'nocturne_app' role to enable RLS enforcement.",
                        user, isSuper, bypassRls);
                }
                else
                {
                    logger.LogInformation(
                        "Row Level Security self-check passed for {Count} tenant-scoped tables (runtime role: {User})",
                        tables.Length, user);
                }
            }
        }
    }

    /// <summary>
    /// Verifies that the runtime connection string does not have NoResetOnClose
    /// enabled. With NoResetOnClose = true, pooled connections skip DISCARD ALL,
    /// allowing stale app.current_tenant_id values to leak across requests.
    /// </summary>
    private static void VerifyNoResetOnClose(NocturneDbContext context, ILogger logger)
    {
        var connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            return;
        }

        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (csb.NoResetOnClose)
        {
            throw new InvalidOperationException(
                "The runtime PostgreSQL connection string has NoResetOnClose = true. " +
                "This prevents DISCARD ALL from running when connections return to the pool, " +
                "which allows stale app.current_tenant_id values to leak across tenants. " +
                "Remove 'No Reset On Close=true' from the connection string.");
        }

        logger.LogDebug("NoResetOnClose check passed for runtime connection string");
    }
}
