using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class ConnectorBackgroundServiceTests
{
    /// <summary>
    /// Minimal IConnectorConfiguration implementation for testing.
    /// </summary>
    private class TestConnectorConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    /// <summary>
    /// Concrete test subclass that returns a preconfigured SyncResult from PerformSyncAsync.
    /// </summary>
    private class TestConnectorBackgroundService : ConnectorBackgroundService<TestConnectorConfig>
    {
        private readonly SyncResult _syncResult;
        private readonly Action? _onSync;
        private readonly Action<IServiceProvider>? _onSyncScope;
        private readonly Action? _onSyncCompleted;
        private readonly TimeSpan? _perTenantTimeout;
        private readonly int _hangFirstNCalls;
        private int _callCount;

        public TestConnectorBackgroundService(
            IServiceProvider serviceProvider,
            SyncResult syncResult,
            ILogger logger,
            Action? onSync = null,
            Action<IServiceProvider>? onSyncScope = null,
            TimeSpan? perTenantTimeout = null,
            int hangFirstNCalls = 0,
            Action? onSyncCompleted = null)
            : base(serviceProvider, logger)
        {
            _syncResult = syncResult;
            _onSync = onSync;
            _onSyncScope = onSyncScope;
            _perTenantTimeout = perTenantTimeout;
            _hangFirstNCalls = hangFirstNCalls;
            _onSyncCompleted = onSyncCompleted;
        }

        protected override string ConnectorName => "TestConnector";

        protected override TimeSpan PerTenantSyncTimeout => _perTenantTimeout ?? base.PerTenantSyncTimeout;

        /// <summary>Number of times PerformSyncAsync has been entered (across all tenants).</summary>
        public int CallCount => _callCount;

        protected override async Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            TestConnectorConfig config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null)
        {
            var n = Interlocked.Increment(ref _callCount);
            _onSync?.Invoke();
            _onSyncScope?.Invoke(scopeProvider);

            // Simulate a stuck tenant (e.g. an auth-retry storm) that respects cancellation.
            if (n <= _hangFirstNCalls)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            _onSyncCompleted?.Invoke();
            return _syncResult;
        }

        /// <summary>
        /// Triggers a single sync cycle by invoking the private SyncAllTenantsAsync
        /// via reflection. Avoids timer-based timing issues.
        /// </summary>
        public async Task ExecuteOnceAsync(CancellationToken ct)
        {
            var method = typeof(ConnectorBackgroundService<TestConnectorConfig>)
                .GetMethod("SyncAllTenantsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)method.Invoke(this, [ct])!;
        }

        /// <summary>
        /// Exposes the protected RequestImmediateSync for testing.
        /// </summary>
        public new void RequestImmediateSync(Guid tenantId) => base.RequestImmediateSync(tenantId);
    }

    /// <summary>
    /// Sets up an in-memory SQLite NocturneDbContext with one active tenant.
    /// </summary>
    private static (IDisposable cleanup, string connectionString) CreateSqliteDb()
    {
        var (cleanup, connectionString, _) = CreateSqliteDbWithTenantId();
        return (cleanup, connectionString);
    }

    /// <summary>
    /// Sets up an in-memory SQLite NocturneDbContext with one active tenant,
    /// returning the tenant ID for tests that need to target a specific tenant.
    /// </summary>
    private static (IDisposable cleanup, string connectionString, Guid tenantId) CreateSqliteDbWithTenantId()
    {
        // Use a temp file so factory-created contexts can share the same data
        var dbPath = Path.Combine(Path.GetTempPath(), $"ConnectorBgTest_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        var cleanup = new TempFileCleanup(dbPath);

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var context = new NocturneDbContext(options);
        // Create just the Tenants table -- we only need that for the background service query
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

        var tenantId = Guid.NewGuid();
        context.Database.ExecuteSqlRaw(
            "INSERT INTO tenants (Id, slug, display_name, is_active, allow_access_requests, sys_created_at, sys_updated_at) VALUES ({0}, {1}, {2}, 1, 1, {3}, {4})",
            tenantId.ToString(), "test-tenant", "Test Tenant",
            DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));

        return (cleanup, connectionString, tenantId);
    }

    /// <summary>
    /// Sets up an in-memory SQLite NocturneDbContext with two active tenants.
    /// </summary>
    private static (IDisposable cleanup, string connectionString) CreateSqliteDbWithTwoTenants()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ConnectorBgTest_{Guid.NewGuid():N}.db");
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

        foreach (var slug in new[] { "tenant-a", "tenant-b" })
        {
            context.Database.ExecuteSqlRaw(
                "INSERT INTO tenants (Id, slug, display_name, is_active, allow_access_requests, sys_created_at, sys_updated_at) VALUES ({0}, {1}, {2}, 1, 1, {3}, {4})",
                Guid.NewGuid().ToString(), slug, slug,
                DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));
        }

        return (cleanup, connectionString);
    }

    private static IServiceProvider BuildServiceProvider(
        string connectionString,
        Mock<IConnectorConfigurationService> configServiceMock,
        TestConnectorConfig config)
    {
        var services = new ServiceCollection();

        // Register IDbContextFactory<NocturneDbContext> and scoped NocturneDbContext,
        // both backed by the shared in-memory SQLite database.
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(
            new SqliteDbContextFactory(connectionString));

        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            return factory.CreateDbContext();
        });

        // Register scoped services
        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = new Mock<ITenantAccessor>();
            mock.Setup(t => t.IsResolved).Returns(true);
            mock.Setup(t => t.TenantId).Returns(Guid.NewGuid());
            mock.Setup(t => t.SetTenant(It.IsAny<TenantContext>()));
            return mock.Object;
        });

        services.AddScoped<IConnectorConfigurationService>(_ => configServiceMock.Object);

        // Register config loader that returns the test config
        services.AddScoped<IConnectorConfigurationLoader<TestConnectorConfig>>(
            _ => new TestConfigLoader(config));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FailedSync_WithErrors_PropagatesErrorMessagesToHealthState()
    {
        // Arrange
        var (cleanup, connStr) = CreateSqliteDb();
        using var _ = cleanup;

        var errorMessages = new List<string> { "Connection refused", "Timeout after 30s" };
        var syncResult = new SyncResult
        {
            Success = false,
            Message = "Fallback message",
            Errors = errorMessages
        };

        var configServiceMock = new Mock<IConnectorConfigurationService>();

        // GetConfigurationAsync must return a config so the sync path proceeds
        configServiceMock
            .Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 5}")
            });

        configServiceMock
            .Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        configServiceMock
            .Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance);

        // Act
        await sut.ExecuteOnceAsync(CancellationToken.None);

        // Assert — verify UpdateHealthStateAsync was called with the joined error messages
        var expectedErrorMessage = "Connection refused; Timeout after 30s";

        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector",
                It.IsAny<DateTime?>(),    // lastSyncAttempt
                It.IsAny<DateTime?>(),    // lastSuccessfulSync
                expectedErrorMessage,     // lastErrorMessage — the key assertion
                It.IsAny<DateTime?>(),    // lastErrorAt
                false,                    // isHealthy
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Expected the specific error messages from SyncResult.Errors to be passed to UpdateHealthStateAsync");
    }

    [Fact]
    public async Task FailedSync_WithNoErrors_FallsBackToMessage()
    {
        // Arrange
        var (cleanup, connStr) = CreateSqliteDb();
        using var _ = cleanup;

        var syncResult = new SyncResult
        {
            Success = false,
            Message = "Custom failure message",
            Errors = [] // empty errors list
        };

        var configServiceMock = new Mock<IConnectorConfigurationService>();

        configServiceMock
            .Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 5}")
            });

        configServiceMock
            .Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        configServiceMock
            .Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance);

        // Act
        await sut.ExecuteOnceAsync(CancellationToken.None);

        // Assert — should fall back to SyncResult.Message when Errors is empty
        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector",
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                "Custom failure message",
                It.IsAny<DateTime?>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Expected SyncResult.Message to be used when Errors list is empty");
    }

    [Fact]
    public async Task FailedSync_WithNoErrorsAndNoMessage_FallsBackToDefault()
    {
        // Arrange
        var (cleanup, connStr) = CreateSqliteDb();
        using var _ = cleanup;

        var syncResult = new SyncResult
        {
            Success = false,
            Message = "",
            Errors = []
        };

        var configServiceMock = new Mock<IConnectorConfigurationService>();

        configServiceMock
            .Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 5}")
            });

        configServiceMock
            .Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        configServiceMock
            .Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance);

        // Act
        await sut.ExecuteOnceAsync(CancellationToken.None);

        // Assert — should fall back to "Sync failed" when both Errors and Message are empty
        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector",
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                "Sync failed",
                It.IsAny<DateTime?>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Expected default 'Sync failed' message when both Errors and Message are empty");
    }

    [Fact]
    public async Task SuccessfulSync_ClearsErrorMessage()
    {
        // Arrange
        var (cleanup, connStr) = CreateSqliteDb();
        using var _ = cleanup;

        var syncResult = new SyncResult
        {
            Success = true,
            Message = "OK"
        };

        var configServiceMock = new Mock<IConnectorConfigurationService>();

        configServiceMock
            .Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 5}")
            });

        configServiceMock
            .Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        configServiceMock
            .Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance);

        // Act
        await sut.ExecuteOnceAsync(CancellationToken.None);

        // Assert — on success, error message should be cleared (empty string)
        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector",
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                string.Empty,             // error message cleared
                It.IsAny<DateTime?>(),
                true,                     // isHealthy = true
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Expected error message to be cleared on successful sync");
    }

    [Fact]
    public async Task SyncForTenant_PinsScopedDbContextToSyncedTenant_ForRlsIsolation()
    {
        // Regression test for the connector-wide outage: NocturneDbContext is leased from a pool
        // (AddPooledDbContextFactory) and its TenantId is NOT reset between leases. The background
        // sync must set dbContext.TenantId to the tenant being synced — otherwise the
        // TenantConnectionInterceptor applies a stale/empty RLS tenant, tenant-scoped reads
        // (connector config + secrets) silently return nothing, and every connector authenticates
        // with empty credentials. Before the fix the scoped context stayed at Guid.Empty here.
        var (cleanup, connStr, tenantId) = CreateSqliteDbWithTenantId();
        using var _ = cleanup;

        var syncResult = new SyncResult { Success = true, Message = "OK" };

        var configServiceMock = new Mock<IConnectorConfigurationService>();
        configServiceMock
            .Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 5}")
            });
        configServiceMock
            .Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        configServiceMock
            .Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };
        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        Guid? capturedTenantId = null;
        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSyncScope: sp => capturedTenantId = sp.GetRequiredService<NocturneDbContext>().TenantId);

        // Act
        await sut.ExecuteOnceAsync(CancellationToken.None);

        // Assert — the scoped DbContext the connector services share must be pinned to the synced
        // tenant so RLS scopes correctly.
        Assert.Equal(tenantId, capturedTenantId);
    }

    [Fact]
    public async Task SyncAllTenants_RunsTenantsConcurrently_OneStuckTenantDoesNotBlockOthers()
    {
        // Tenants must sync independently: a tenant whose sync hangs (e.g. an auth-retry storm against
        // bad credentials) must not delay or block any other tenant of the connector.
        var (cleanup, connStr) = CreateSqliteDbWithTwoTenants();
        using var _ = cleanup;

        var configServiceMock = BuildEnabledConfigMock();
        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };
        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var secondTenantDone = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        // The first tenant to start hangs. A long per-tenant timeout ensures this test measures
        // concurrency, not the timeout cutting the hang short.
        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            perTenantTimeout: TimeSpan.FromSeconds(30),
            hangFirstNCalls: 1,
            onSyncCompleted: () => secondTenantDone.TrySetResult());

        var run = sut.ExecuteOnceAsync(cts.Token);
        try
        {
            var winner = await Task.WhenAny(secondTenantDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            winner.Should().Be(secondTenantDone.Task,
                "the second tenant must sync concurrently while the first tenant is stuck");
        }
        finally
        {
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SyncForTenant_IsCancelled_WhenItExceedsPerTenantTimeout()
    {
        // A stuck tenant must be cancelled at PerTenantSyncTimeout so it cannot hold a slot forever.
        var (cleanup, connStr, _) = CreateSqliteDbWithTenantId();
        using var _c = cleanup;

        var configServiceMock = BuildEnabledConfigMock();
        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };
        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            perTenantTimeout: TimeSpan.FromMilliseconds(300),
            hangFirstNCalls: 1);

        var run = sut.ExecuteOnceAsync(CancellationToken.None);
        var winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        winner.Should().Be(run,
            "a tenant exceeding PerTenantSyncTimeout must be cancelled so the cycle completes");
        await run;
    }

    /// <summary>Config-service mock that reports the test connector as configured and enabled.</summary>
    private static Mock<IConnectorConfigurationService> BuildEnabledConfigMock()
    {
        var mock = new Mock<IConnectorConfigurationService>();
        mock.Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 5}")
            });
        mock.Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        mock.Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    [Fact]
    public async Task RequestImmediateSync_CausesNextPollToSyncImmediately()
    {
        // Arrange
        var (cleanup, connStr, tenantId) = CreateSqliteDbWithTenantId();
        using var _ = cleanup;

        var syncCount = 0;
        var syncResult = new SyncResult { Success = true, Message = "OK" };

        var configServiceMock = new Mock<IConnectorConfigurationService>();
        configServiceMock
            .Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 60}")
            });
        configServiceMock
            .Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        configServiceMock
            .Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 60 };
        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSync: () => syncCount++);

        // Act — first poll triggers the initial sync
        await sut.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(1, syncCount);

        // Second poll should NOT sync (60-minute interval hasn't elapsed)
        await sut.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(1, syncCount);

        // Request immediate sync, then poll again — it should sync immediately
        sut.RequestImmediateSync(tenantId);
        await sut.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(2, syncCount);
    }

    [Fact]
    public async Task RequestImmediateSync_DebouncesPreviouslyNudgedTenant()
    {
        // Arrange
        var (cleanup, connStr, tenantId) = CreateSqliteDbWithTenantId();
        using var _ = cleanup;

        var syncCount = 0;
        var syncResult = new SyncResult { Success = true, Message = "OK" };

        var configServiceMock = new Mock<IConnectorConfigurationService>();
        configServiceMock
            .Setup(x => x.GetConfigurationAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "TestConnector",
                IsActive = true,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"syncIntervalMinutes\": 60}")
            });
        configServiceMock
            .Setup(x => x.GetSecretsAsync("TestConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        configServiceMock
            .Setup(x => x.UpdateHealthStateAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 60 };
        var serviceProvider = BuildServiceProvider(connStr, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSync: () => syncCount++);

        // Act — initial sync
        await sut.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(1, syncCount);

        // First nudge should succeed and cause a sync
        sut.RequestImmediateSync(tenantId);
        await sut.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(2, syncCount);

        // Second nudge within the debounce window should be ignored
        sut.RequestImmediateSync(tenantId);
        await sut.ExecuteOnceAsync(CancellationToken.None);
        // Sync count should remain at 2 because the nudge was debounced
        // and the 60-minute interval hasn't elapsed
        Assert.Equal(2, syncCount);
    }

    /// <summary>
    /// Concrete config loader that returns a preconfigured TestConnectorConfig.
    /// </summary>
    private sealed class TestConfigLoader(TestConnectorConfig config) : IConnectorConfigurationLoader<TestConnectorConfig>
    {
        public Task<TestConnectorConfig> LoadForTenantAsync(CancellationToken ct)
            => Task.FromResult(config);
    }

    /// <summary>
    /// Simple IDbContextFactory that creates NocturneDbContext instances
    /// against a SQLite database file.
    /// </summary>
    private sealed class SqliteDbContextFactory(string connectionString) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite(connectionString)
                .Options;
            return new NocturneDbContext(options);
        }
    }

    /// <summary>
    /// Deletes a temporary SQLite database file on dispose.
    /// </summary>
    private sealed class TempFileCleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

}
