using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class NocturneRemoteRealtimeListenerTests
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
        var sut = new NocturneRemoteConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<NocturneRemoteConnectorBackgroundService>.Instance);

        // Act & Assert — should not throw
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);
    }

    /// <summary>
    /// StopRealtimeListenersAsync should be safe to call even when no listeners
    /// have been started (i.e. the hub connection dictionary is empty).
    /// </summary>
    [Fact]
    public async Task StopRealtimeListenersAsync_NoListenersStarted_DoesNotThrow()
    {
        // Arrange
        using var db = TestDbContextFactory.CreateSqlite();

        var serviceProvider = BuildServiceProvider(db);
        var sut = new NocturneRemoteConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<NocturneRemoteConnectorBackgroundService>.Instance);

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
        var sut = new NocturneRemoteConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<NocturneRemoteConnectorBackgroundService>.Instance);

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

        var config = new NocturneRemoteConnectorConfiguration
        {
            Enabled = false,
            Url = "http://remote.example.com",
            AccessToken = "test-token",
        };

        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NocturneRemoteConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<NocturneRemoteConnectorBackgroundService>.Instance);

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

        var config = new NocturneRemoteConnectorConfiguration
        {
            Enabled = true,
            Url = "",
            AccessToken = "test-token",
        };

        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NocturneRemoteConnectorBackgroundService(
            serviceProvider,
            new ConnectorSyncBudget(),
            serviceProvider.GetRequiredService<ActiveTenantSnapshot>(),
            NullLogger<NocturneRemoteConnectorBackgroundService>.Instance);

        // Act & Assert — should skip the tenant without throwing
        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);
    }

    /// <summary>
    /// Pins the listener's own call site: a tenant storing a bare host must reach the connect
    /// step against the resolved absolute hub URI, rather than being turned away by the
    /// absolute-URI guard in front of it.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_SchemelessUrl_ConnectsToResolvedHubUri()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NocturneRemoteConnectorConfiguration
        {
            Enabled = true,
            Url = "127.0.0.1:9",
            AccessToken = "test-token",
        };

        var logger = new ListLogger<NocturneRemoteConnectorBackgroundService>();
        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NocturneRemoteConnectorBackgroundService(
            serviceProvider, new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), logger);

        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("Failed to connect SignalR")
            && e.Message.Contains("https://127.0.0.1:9/hubs/data"));
    }

    /// <summary>
    /// A stored URL the resolver refuses is the listener's to report: it cannot connect, and
    /// letting the rejection reach the per-tenant catch would file it as an unexpected error.
    /// </summary>
    [Fact]
    public async Task StartRealtimeListenersAsync_UnresolvableUrl_ReportsItAndSkipsTenant()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var config = new NocturneRemoteConnectorConfiguration
        {
            Enabled = true,
            Url = "ftp://x",
            AccessToken = "test-token",
        };

        var logger = new ListLogger<NocturneRemoteConnectorBackgroundService>();
        var serviceProvider = BuildServiceProvider(db, config);
        var sut = new NocturneRemoteConnectorBackgroundService(
            serviceProvider, new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), logger);

        await InvokeStartRealtimeListenersAsync(sut, CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("cannot be resolved to an absolute http(s) URL")
            && e.Message.Contains("test-tenant"));
        logger.Entries.Should().NotContain(e =>
            e.Message.Contains("Unexpected error starting real-time listener"));
        logger.Entries.Should().NotContain(e => e.Message.Contains("Failed to connect SignalR"));
    }

    #region Helpers

    /// <summary>
    /// Invokes the protected StartRealtimeListenersAsync via reflection.
    /// </summary>
    private static async Task InvokeStartRealtimeListenersAsync(
        NocturneRemoteConnectorBackgroundService sut,
        CancellationToken cancellationToken)
    {
        var method = typeof(ConnectorBackgroundService<NocturneRemoteConnectorConfiguration>)
            .GetMethod("StartRealtimeListenersAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [cancellationToken])!;
    }

    /// <summary>
    /// Invokes the protected StopRealtimeListenersAsync via reflection.
    /// </summary>
    private static async Task InvokeStopRealtimeListenersAsync(
        NocturneRemoteConnectorBackgroundService sut)
    {
        var method = typeof(ConnectorBackgroundService<NocturneRemoteConnectorConfiguration>)
            .GetMethod("StopRealtimeListenersAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [])!;
    }

    /// <summary>
    /// Builds a service provider wired up for the NocturneRemoteConnectorBackgroundService.
    /// When <paramref name="config"/> is null, no config loader is registered (used for
    /// the "no tenants" scenario where it's never resolved).
    /// </summary>
    private static IServiceProvider BuildServiceProvider(
        SqliteTestDatabase db,
        NocturneRemoteConnectorConfiguration? config = null)
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
            services.AddScoped<IConnectorConfigurationLoader<NocturneRemoteConnectorConfiguration>>(
                _ => new StaticConfigLoader(config));
        }
        else
        {
            // Register a loader that throws — it should never be called for empty tenant lists
            services.AddScoped<IConnectorConfigurationLoader<NocturneRemoteConnectorConfiguration>>(
                _ => throw new InvalidOperationException("Config loader should not be called when there are no tenants"));
        }

        return services.BuildServiceProvider();
    }

    private sealed class StaticConfigLoader(NocturneRemoteConnectorConfiguration config)
        : IConnectorConfigurationLoader<NocturneRemoteConnectorConfiguration>
    {
        public Task<NocturneRemoteConnectorConfiguration> LoadForTenantAsync(CancellationToken ct)
            => Task.FromResult(config);
    }

    #endregion
}
