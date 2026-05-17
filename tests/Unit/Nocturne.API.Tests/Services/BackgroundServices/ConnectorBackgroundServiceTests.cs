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

        public TestConnectorBackgroundService(
            IServiceProvider serviceProvider,
            SyncResult syncResult,
            ILogger logger)
            : base(serviceProvider, logger)
        {
            _syncResult = syncResult;
        }

        protected override string ConnectorName => "TestConnector";

        protected override Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            TestConnectorConfig config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null)
        {
            return Task.FromResult(_syncResult);
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
    }

    /// <summary>
    /// Sets up an in-memory SQLite NocturneDbContext with one active tenant.
    /// </summary>
    private static (IDisposable cleanup, string connectionString) CreateSqliteDb()
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
