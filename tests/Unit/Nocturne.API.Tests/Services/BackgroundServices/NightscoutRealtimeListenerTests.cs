using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
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
        using var db = TestDbContextFactory.CreateSqlite();

        var serviceProvider = BuildServiceProvider(db);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
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
        using var db = TestDbContextFactory.CreateSqlite();

        var serviceProvider = BuildServiceProvider(db);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
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
        using var db = TestDbContextFactory.CreateSqlite();

        var serviceProvider = BuildServiceProvider(db);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
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
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = false,
            Url = "http://nightscout.example.com",
        };

        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
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
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "",
        };

        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
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
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "http://127.0.0.1:9",
        };

        var logger = new ListLogger<NightscoutConnectorBackgroundService>();
        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NightscoutConnectorBackgroundService(serviceProvider, new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), logger);

        // Act
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        // Assert — the connect failed (nothing is listening on port 9), but it must have failed by
        // actually attempting to connect, not by rejecting our own options.
        Assert.DoesNotContain(logger.Entries, e => e.Exception is ArgumentOutOfRangeException);
    }

    /// <summary>
    /// Tenants may store a bare host with no scheme; the polling path normalises this via
    /// <see cref="ConnectorUrl.ResolveBase"/>. The listener must reach the connect step against
    /// the same resolved origin rather than be turned away by its absolute-URI guard.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_SchemelessUrl_ConnectsToResolvedOrigin()
    {
        // Arrange — a bare host, as three production tenants have stored
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "127.0.0.1:9",
        };

        var logger = new ListLogger<NightscoutConnectorBackgroundService>();
        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NightscoutConnectorBackgroundService(serviceProvider, new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), logger);

        // Act
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(e =>
            e.Message.Contains("Failed to connect Socket.IO")
            && e.Message.Contains("https://127.0.0.1:9"));
    }

    /// <summary>
    /// A deployment can keep polling behind a restricted REST adapter while connecting the
    /// listener to the original Nightscout Socket.IO origin.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_RealtimeUrlConfigured_UsesRealtimeOrigin()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "http://rest-adapter.invalid",
            RealtimeUrl = "http://127.0.0.1:9",
        };

        var logger = new ListLogger<NightscoutConnectorBackgroundService>();
        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NightscoutConnectorBackgroundService(serviceProvider, new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), logger);

        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("Failed to connect Socket.IO")
            && e.Message.Contains("http://127.0.0.1:9"));
        logger.Entries.Should().NotContain(e => e.Message.Contains("rest-adapter.invalid"));
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
        var tenantId = Guid.NewGuid();
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant");

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "http://127.0.0.1:9",
        };

        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<NightscoutConnectorBackgroundService>.Instance);

        var dead = new SocketIO(new Uri("http://127.0.0.1:9"));
        SocketClients(sut)[tenantId] = dead;

        // Act
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        // Assert — the dead client is gone (the replacement connect fails; the tenant polls meanwhile)
        Assert.DoesNotContain(dead, SocketClients(sut).Values);
    }

    /// <summary>
    /// A stored URL the resolver refuses is the listener's to report: it cannot connect, and
    /// letting the rejection reach the per-tenant catch would file it as an unexpected error.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_UnresolvableUrl_ReportsItAndSkipsTenant()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NightscoutConnectorConfiguration
        {
            Enabled = true,
            Url = "ftp://x",
        };

        var logger = new ListLogger<NightscoutConnectorBackgroundService>();
        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NightscoutConnectorBackgroundService(
            serviceProvider, new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), logger);

        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("cannot be resolved to an absolute http(s) URL")
            && e.Message.Contains("test-tenant"));
        logger.Entries.Should().NotContain(e =>
            e.Message.Contains("Unexpected error starting real-time listener"));
        logger.Entries.Should().NotContain(e => e.Message.Contains("Failed to connect Socket.IO"));
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
    /// Builds a service provider wired up for the NightscoutConnectorBackgroundService.
    /// When <paramref name="config"/> is null, no config loader is registered (used for
    /// the "no tenants" scenario where it's never resolved).
    /// </summary>
    private static IServiceProvider BuildServiceProvider(
        SqliteTestDatabase db,
        NightscoutConnectorConfiguration? config = null)
    {
        var services = new ServiceCollection();

        db.AddToServices(services);
        services.AddActiveTenantSnapshot();

        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = MockTenantAccessor.Create(Guid.NewGuid());
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

    #endregion
}
