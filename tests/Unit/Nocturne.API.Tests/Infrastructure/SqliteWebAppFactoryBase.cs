using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Runs the real <c>Program.cs</c> pipeline over a SQLite in-memory store. The schema is built
/// from the model rather than the migrations, which carry PostgreSQL-specific SQL.
/// </summary>
public abstract class SqliteWebAppFactoryBase<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    private SqliteConnection? _connection;

    public SqliteConnection Connection => _connection
        ?? throw new InvalidOperationException("Factory not initialized");

    protected abstract string InstanceKey { get; }

    /// <summary>
    /// SHA-1 of the API secret to store on the seeded tenant, or null for a tenant with no
    /// API secret configured.
    /// </summary>
    protected virtual string? ApiSecretHash => null;

    // Force minimal hosting path — without this override, WebApplicationFactory discovers
    // the global Program.CreateHostBuilder (used by NSwag) and uses NSwagStartup instead
    // of the real Program.cs pipeline, resulting in zero mapped endpoints.
    protected override IHostBuilder? CreateHostBuilder() => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Environment"] = "Testing",
                ["Features:EnableExternalConnectors"] = "false",
                ["Features:EnableRealTimeNotifications"] = "false",
                ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                ["Logging:LogLevel:Default"] = "Error",
                ["Logging:LogLevel:Microsoft"] = "Error",
                ["Logging:LogLevel:System"] = "Error",
                ["INSTANCE_KEY"] = InstanceKey,
            });
        });

        builder.ConfigureServices(services =>
        {
            RemoveDbContextRegistrations(services);
            RemoveService<ICacheService>(services);

            var conn = Connection;
            var mockFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
            mockFactory.Setup(f => f.CreateDbContext()).Returns(() => new NocturneDbContext(
                new DbContextOptionsBuilder<NocturneDbContext>()
                    .UseSqlite(conn)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options));
            mockFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => mockFactory.Object.CreateDbContext());
            services.AddSingleton(mockFactory.Object);

            ConfigureTestServices(services);

            // Required by the global ReadAccessAuditFilter, which the real pipeline registers
            // alongside the PostgreSQL infrastructure this factory bypasses.
            services.AddSingleton<ITenantAuditConfigCache, TenantAuditConfigCache>();

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            db.Database.EnsureCreated();
            TestDatabaseSeeder.Seed(db, ApiSecretHash);

            RemoveHostedServices(services);
        });

        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(ConfigureTestLogging);
    }

    /// <summary>
    /// Registers the <see cref="NocturneDbContext"/> resolution the seeding step and the request
    /// pipeline both depend on, plus whatever else the suite stubs out.
    /// </summary>
    protected abstract void ConfigureTestServices(IServiceCollection services);

    protected virtual void ConfigureTestLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(LogLevel.Warning);
    }

    protected static void RemoveService<T>(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var descriptors = services
            .Where(d => d.ServiceType.Name.Contains("DbContext")
                || d.ServiceType.Name.Contains("Migration")
                || d.ServiceType.Name.Contains("Database"))
            .ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }

    private static void RemoveHostedServices(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection?.Dispose();
        }
    }
}
