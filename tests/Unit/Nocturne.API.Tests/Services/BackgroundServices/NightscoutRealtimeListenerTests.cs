using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class NightscoutRealtimeListenerTests
{
    /// <summary>
    /// StartRealtimeListenersAsync should complete without throwing when there
    /// are no active tenants in the database.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_NoTenants_DoesNotThrow()
    {
        // Arrange — empty database (no tenants)
        var (cleanup, connectionString) = CreateSqliteDb(addTenant: false);
        using var _ = cleanup;

        var serviceProvider = BuildServiceProvider(connectionString);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            NullLogger<NightscoutConnectorBackgroundService>.Instance);

        // Act & Assert — should not throw
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);
    }

    /// <summary>
    /// StopRealtimeListenersAsync should be safe to call even when no listeners
    /// have been started (i.e. the socket client dictionary is empty).
    /// </summary>
    [Fact]
    public async Task StopRealtimeListenersAsync_NoListenersStarted_DoesNotThrow()
    {
        // Arrange
        var (cleanup, connectionString) = CreateSqliteDb(addTenant: false);
        using var _ = cleanup;

        var serviceProvider = BuildServiceProvider(connectionString);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            NullLogger<NightscoutConnectorBackgroundService>.Instance);

        // Act & Assert — should not throw
        await InvokeStopRealtimeListenersAsync(sut);
    }

    /// <summary>
    /// StopRealtimeListenersAsync should be safe to call multiple times in a row.
    /// </summary>
    [Fact]
    public async Task StopRealtimeListenersAsync_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var (cleanup, connectionString) = CreateSqliteDb(addTenant: false);
        using var _ = cleanup;

        var serviceProvider = BuildServiceProvider(connectionString);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            NullLogger<NightscoutConnectorBackgroundService>.Instance);

        // Act & Assert — should not throw on repeated calls
        await InvokeStopRealtimeListenersAsync(sut);
        await InvokeStopRealtimeListenersAsync(sut);
    }

    /// <summary>
    /// When a tenant exists but the connector config is disabled, StartRealtimeListenersAsync
    /// should skip that tenant without throwing.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_DisabledConnector_SkipsTenant()
    {
        // Arrange — one tenant with a disabled connector config
        var (cleanup, connectionString) = CreateSqliteDb(addTenant: true);
        using var _ = cleanup;

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = false,
            Url = "http://nightscout.example.com",
        };

        var serviceProvider = BuildServiceProvider(connectionString, config);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            NullLogger<NightscoutConnectorBackgroundService>.Instance);

        // Act & Assert — should skip the tenant without throwing
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);
    }

    /// <summary>
    /// When a tenant exists but the connector config has no URL, StartRealtimeListenersAsync
    /// should skip that tenant without throwing.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_EmptyUrl_SkipsTenant()
    {
        // Arrange — one tenant with no URL configured
        var (cleanup, connectionString) = CreateSqliteDb(addTenant: true);
        using var _ = cleanup;

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "",
        };

        var serviceProvider = BuildServiceProvider(connectionString, config);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            NullLogger<NightscoutConnectorBackgroundService>.Instance);

        // Act & Assert — should skip the tenant without throwing
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);
    }

    #region Helpers

    /// <summary>
    /// Invokes the protected StartRealtimeListenersAsync via reflection.
    /// </summary>
    private static async Task InvokeStartRealtimeListenersAsync(
        NightscoutConnectorBackgroundService sut,
        CancellationToken cancellationToken)
    {
        var method = typeof(ConnectorBackgroundService<NightscoutConnectorConfiguration>)
            .GetMethod("StartRealtimeListenersAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [cancellationToken])!;
    }

    /// <summary>
    /// Invokes the protected StopRealtimeListenersAsync via reflection.
    /// </summary>
    private static async Task InvokeStopRealtimeListenersAsync(
        NightscoutConnectorBackgroundService sut)
    {
        var method = typeof(ConnectorBackgroundService<NightscoutConnectorConfiguration>)
            .GetMethod("StopRealtimeListenersAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [])!;
    }

    /// <summary>
    /// Creates an in-memory SQLite database, optionally seeding one active tenant.
    /// </summary>
    private static (IDisposable cleanup, string connectionString) CreateSqliteDb(bool addTenant)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"NsRealtimeTest_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        var cleanup = new TempFileCleanup(dbPath);

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var context = new NocturneDbContext(options);
        context.Database.ExecuteSqlRaw(@"
            CREATE TABLE tenants (
                Id TEXT PRIMARY KEY,
                slug TEXT NOT NULL,
                display_name TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                last_reading_at TEXT,
                allow_access_requests INTEGER NOT NULL DEFAULT 1,
                onboarding_completed_at TEXT,
                sys_created_at TEXT NOT NULL,
                sys_updated_at TEXT NOT NULL
            )");

        if (addTenant)
        {
            var tenantId = Guid.NewGuid();
            context.Database.ExecuteSqlRaw(
                "INSERT INTO tenants (Id, slug, display_name, is_active, allow_access_requests, sys_created_at, sys_updated_at) VALUES ({0}, {1}, {2}, 1, 1, {3}, {4})",
                tenantId.ToString(), "test-tenant", "Test Tenant",
                DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));
        }

        return (cleanup, connectionString);
    }

    /// <summary>
    /// Builds a service provider wired up for the NightscoutConnectorBackgroundService.
    /// When <paramref name="config"/> is null, no config loader is registered (used for
    /// the "no tenants" scenario where it's never resolved).
    /// </summary>
    private static IServiceProvider BuildServiceProvider(
        string connectionString,
        NightscoutConnectorConfiguration? config = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(
            new SqliteDbContextFactory(connectionString));

        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            return factory.CreateDbContext();
        });

        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = new Mock<ITenantAccessor>();
            mock.Setup(t => t.IsResolved).Returns(true);
            mock.Setup(t => t.TenantId).Returns(Guid.NewGuid());
            mock.Setup(t => t.SetTenant(It.IsAny<TenantContext>()));
            return mock.Object;
        });

        if (config != null)
        {
            services.AddScoped<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>(
                _ => new StaticConfigLoader(config));
        }
        else
        {
            // Register a loader that throws — it should never be called for empty tenant lists
            services.AddScoped<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>(
                _ => throw new InvalidOperationException("Config loader should not be called when there are no tenants"));
        }

        return services.BuildServiceProvider();
    }

    private sealed class StaticConfigLoader(NightscoutConnectorConfiguration config)
        : IConnectorConfigurationLoader<NightscoutConnectorConfiguration>
    {
        public Task<NightscoutConnectorConfiguration> LoadForTenantAsync(CancellationToken ct)
            => Task.FromResult(config);
    }

    private sealed class SqliteDbContextFactory(string connectionString)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite(connectionString)
                .Options;
            return new NocturneDbContext(options);
        }
    }

    private sealed class TempFileCleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    #endregion
}
