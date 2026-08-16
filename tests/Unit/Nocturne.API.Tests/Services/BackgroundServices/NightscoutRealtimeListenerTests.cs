using System.Collections.Concurrent;
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
using SocketIOClient;
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

    /// <summary>
    /// SocketIOClient bounds the whole connect-with-retries operation with
    /// <c>new CancellationTokenSource(ReconnectionAttempts * ReconnectionDelayMax)</c>, evaluated in
    /// int arithmetic. A product above <see cref="int.MaxValue"/> wraps negative and ConnectAsync
    /// throws ArgumentOutOfRangeException before attempting a single connection, so every tenant
    /// silently loses its real-time listener.
    /// </summary>
    [Fact]
    public void ReconnectionBudget_FitsWithinInt()
    {
        var product = (long)NightscoutConnectorBackgroundService.ReconnectionAttempts
            * NightscoutConnectorBackgroundService.ReconnectionDelayMaxMs;

        Assert.InRange(product, 1, int.MaxValue);
    }

    /// <summary>
    /// A tenant with an enabled connector pointing at an unreachable instance must fall back to
    /// polling. Regression test for the reconnection-budget overflow: the resulting
    /// ArgumentOutOfRangeException was swallowed by the per-tenant catch, so the only visible
    /// symptom was a recurring warning and no listener ever starting.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_UnreachableInstance_DoesNotFailOnReconnectionBudget()
    {
        // Arrange — one tenant pointing at a closed port (discard service)
        var (cleanup, connectionString) = CreateSqliteDb(addTenant: true);
        using var _ = cleanup;

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "http://127.0.0.1:9",
        };

        var logger = new CapturingLogger();
        var serviceProvider = BuildServiceProvider(connectionString, config);
        var sut = new NightscoutConnectorBackgroundService(serviceProvider, logger);

        // Act
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        // Assert — the connect failed (nothing is listening on port 9), but it must have failed by
        // actually attempting to connect, not by rejecting our own options.
        Assert.DoesNotContain(logger.Exceptions, ex => ex is ArgumentOutOfRangeException);
    }

    /// <summary>
    /// Tenants may store a bare host with no scheme; the polling path normalises this via
    /// ResolveBaseUrl. The listener path must not blow up on Uri parsing where polling succeeds.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_SchemelessUrl_DoesNotThrowUriFormatException()
    {
        // Arrange — a bare host, as three production tenants have stored
        var (cleanup, connectionString) = CreateSqliteDb(addTenant: true);
        using var _ = cleanup;

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "127.0.0.1:9",
        };

        var logger = new CapturingLogger();
        var serviceProvider = BuildServiceProvider(connectionString, config);
        var sut = new NightscoutConnectorBackgroundService(serviceProvider, logger);

        // Act
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        // Assert
        Assert.DoesNotContain(logger.Exceptions, ex => ex is UriFormatException);
    }

    /// <summary>
    /// A socket that exhausts its reconnection budget stops trying and reports Connected == false, but
    /// stays in the tracking dictionary. A repeat listener-startup pass must evict and dispose it so a
    /// fresh client can take its place, rather than treating the tenant as already covered.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_TrackedClientDisconnected_EvictsDeadClient()
    {
        // Arrange — one tenant with an already-tracked client that is not connected
        var (cleanup, connectionString, tenantId) = CreateSqliteDbWithTenantId(addTenant: true);
        using var _ = cleanup;

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "http://127.0.0.1:9",
        };

        var serviceProvider = BuildServiceProvider(connectionString, config);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            NullLogger<NightscoutConnectorBackgroundService>.Instance);

        var dead = new SocketIO(new Uri("http://127.0.0.1:9"));
        SocketClients(sut)[tenantId] = dead;

        // Act
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        // Assert — the dead client is gone (the replacement connect fails; the tenant polls meanwhile)
        Assert.DoesNotContain(dead, SocketClients(sut).Values);
    }

    #region Helpers

    /// <summary>
    /// Reaches the private per-tenant Socket.IO client dictionary.
    /// </summary>
    private static ConcurrentDictionary<Guid, SocketIO> SocketClients(NightscoutConnectorBackgroundService sut)
        => (ConcurrentDictionary<Guid, SocketIO>)typeof(NightscoutConnectorBackgroundService)
            .GetField("_socketClients", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(sut)!;

    /// <summary>
    /// Captures exceptions passed to the logger so tests can assert on how a failure surfaced.
    /// </summary>
    private sealed class CapturingLogger : ILogger<NightscoutConnectorBackgroundService>
    {
        private readonly List<Exception> _exceptions = [];

        public IReadOnlyList<Exception> Exceptions
        {
            get { lock (_exceptions) return _exceptions.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null)
                return;

            lock (_exceptions)
                _exceptions.Add(exception);
        }
    }

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
        var (cleanup, connectionString, _) = CreateSqliteDbWithTenantId(addTenant);
        return (cleanup, connectionString);
    }

    /// <inheritdoc cref="CreateSqliteDb"/>
    private static (IDisposable cleanup, string connectionString, Guid tenantId) CreateSqliteDbWithTenantId(bool addTenant)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"NsRealtimeTest_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        var cleanup = new TempFileCleanup(dbPath);
        var tenantId = Guid.Empty;

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
            tenantId = Guid.NewGuid();
            context.Database.ExecuteSqlRaw(
                "INSERT INTO tenants (Id, slug, display_name, is_active, allow_access_requests, sys_created_at, sys_updated_at) VALUES ({0}, {1}, {2}, 1, 1, {3}, {4})",
                tenantId.ToString(), "test-tenant", "Test Tenant",
                DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));
        }

        return (cleanup, connectionString, tenantId);
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
