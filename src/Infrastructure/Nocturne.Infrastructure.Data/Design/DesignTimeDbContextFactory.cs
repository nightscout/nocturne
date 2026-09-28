using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Nocturne.Infrastructure.Data.Design;

/// <summary>
/// Design-time factory for NocturneDbContext to support Entity Framework migrations
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NocturneDbContext>
{
    /// <summary>
    /// Creates a new instance of NocturneDbContext for design-time operations
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>A configured NocturneDbContext instance</returns>
    public NocturneDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NocturneDbContext>();

        // EF CLI operations need DDL privileges, which only nocturne_migrator
        // has. Prefer the migrator connection string; fall back to the app
        // string only with a loud warning because EF operations will likely
        // fail under the runtime role.
        string? connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__nocturne-postgres-migrator")
            ?? TryGetConnectionStringFromConfig(Directory.GetCurrentDirectory(), "nocturne-postgres-migrator");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString =
                Environment.GetEnvironmentVariable("ConnectionStrings__nocturne-postgres")
                ?? Environment.GetEnvironmentVariable("PostgreSql__ConnectionString")
                ?? TryGetConnectionStringFromConfig(Directory.GetCurrentDirectory(), "nocturne-postgres")
                ?? TryGetPostgreSqlFromConfig(Directory.GetCurrentDirectory());

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine(
                    "Warning: ConnectionStrings:nocturne-postgres-migrator not set, falling back to nocturne-postgres. " +
                    "EF CLI operations may fail if the fallback connection uses the runtime app role which lacks DDL privileges.");
            }
        }

        // Fallback to Docker Compose defaults exposed on localhost, using the
        // migrator role so design-time ops (migrations add/update) can issue DDL.
        connectionString ??=
            "Host=localhost;Port=5432;Database=nocturne;Username=nocturne_migrator;Password=dev-migrator-password-change-me";

        optionsBuilder.UseNpgsql(connectionString, npgsql => npgsql.UseNocturneMigrations());

        return new NocturneDbContext(optionsBuilder.Options);
    }

    private static string? TryGetConnectionStringFromConfig(string basePath, string name)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables();

        var config = builder.Build();
        return config.GetConnectionString(name);
    }

    private static string? TryGetPostgreSqlFromConfig(string basePath)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables();

        var config = builder.Build();
        return config.GetSection("PostgreSql:ConnectionString").Value;
    }
}
