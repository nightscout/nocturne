using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
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
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class ConnectorBackgroundServiceTests
{
    /// <summary>
    /// Minimal IConnectorConfiguration implementation for testing.
    /// </summary>
    private class TestConnectorConfig : IConnectorConfiguration
    {
        public ConnectSource ConnectSource { get; set; } = ConnectSource.Nightscout;
        public bool Enabled { get; set; } = true;
        public int MaxRetryAttempts { get; set; } = 1;
        public int BatchSize { get; set; } = 100;
        public int SyncIntervalMinutes { get; set; } = 5;
        public Core.Models.V4.GlucoseProcessing GlucoseProcessing { get; set; } = Core.Models.V4.GlucoseProcessing.Smoothed;
        public void Validate() { }
        public bool IsDataTypeEnabled(SyncDataType type) => true;
        public List<SyncDataType> GetEnabledDataTypes(List<SyncDataType> supportedTypes) => supportedTypes;
    }

    /// <summary>
    /// Concrete test subclass that returns a preconfigured SyncResult from PerformSyncAsync.
    /// </summary>
    private class TestConnectorBackgroundService : ConnectorBackgroundService<TestConnectorConfig>
    {
        private readonly SyncResult _syncResult;

        public TestConnectorBackgroundService(
            IServiceProvider serviceProvider,
            TestConnectorConfig config,
            SyncResult syncResult,
            ILogger logger)
            : base(serviceProvider, config, logger)
        {
            _syncResult = syncResult;
        }

        protected override string ConnectorName => "TestConnector";

        protected override Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null)
        {
            return Task.FromResult(_syncResult);
        }

        /// <summary>
        /// Exposes ExecuteAsync for testing so we can trigger a sync cycle.
        /// </summary>
        public async Task ExecuteOnceAsync(CancellationToken ct)
        {
            // Call the base ExecuteAsync via StartAsync, but that uses a timer.
            // Instead, replicate the sync path by calling the private SyncAllTenantsAsync
            // through reflection, or just call StartAsync with a short cancellation.
            // The simplest: use StartAsync + cancel quickly.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Cancel after a short window so we only do one iteration
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await StartAsync(cts.Token);
                // Wait long enough for the initial delay (5s) + one sync cycle
                await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            finally
            {
                try { await StopAsync(CancellationToken.None); }
                catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Sets up an in-memory SQLite NocturneDbContext with one active tenant.
    /// </summary>
    private static (SqliteConnection connection, DbContextOptions<NocturneDbContext> options) CreateSqliteDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new NocturneDbContext(options);
        // Create just the Tenants table — we only need that for the background service query
        context.Database.ExecuteSqlRaw(@"
            CREATE TABLE tenants (
                Id TEXT PRIMARY KEY,
                slug TEXT NOT NULL,
                display_name TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                is_default INTEGER NOT NULL DEFAULT 0,
                last_reading_at TEXT,
                timezone TEXT NOT NULL DEFAULT 'UTC',
                subject_name TEXT,
                allow_access_requests INTEGER NOT NULL DEFAULT 1,
                sys_created_at TEXT NOT NULL,
                sys_updated_at TEXT NOT NULL
            )");

        var tenantId = Guid.NewGuid();
        context.Database.ExecuteSqlRaw(
            "INSERT INTO tenants (Id, slug, display_name, is_active, is_default, timezone, allow_access_requests, sys_created_at, sys_updated_at) VALUES ({0}, {1}, {2}, 1, 0, 'UTC', 1, {3}, {4})",
            tenantId.ToString(), "test-tenant", "Test Tenant",
            DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));

        return (connection, options);
    }

    private static IServiceProvider BuildServiceProvider(
        DbContextOptions<NocturneDbContext> dbOptions,
        Mock<IConnectorConfigurationService> configServiceMock)
    {
        var services = new ServiceCollection();

        // Register IDbContextFactory<NocturneDbContext>
        services.AddDbContextFactory<NocturneDbContext>(opts =>
        {
            // Copy the SQLite connection from the provided options
            var sqliteExtension = dbOptions.Extensions
                .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
                .First();
            opts.UseSqlite(sqliteExtension.Connection!);
        });

        // Register scoped services
        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = new Mock<ITenantAccessor>();
            return mock.Object;
        });

        services.AddScoped<IConnectorConfigurationService>(_ => configServiceMock.Object);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FailedSync_WithErrors_PropagatesErrorMessagesToHealthState()
    {
        // Arrange
        var (connection, dbOptions) = CreateSqliteDb();
        using var _ = connection;

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

        var serviceProvider = BuildServiceProvider(dbOptions, configServiceMock);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            config,
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
        var (connection, dbOptions) = CreateSqliteDb();
        using var _ = connection;

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

        var serviceProvider = BuildServiceProvider(dbOptions, configServiceMock);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            config,
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
        var (connection, dbOptions) = CreateSqliteDb();
        using var _ = connection;

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

        var serviceProvider = BuildServiceProvider(dbOptions, configServiceMock);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            config,
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
        var (connection, dbOptions) = CreateSqliteDb();
        using var _ = connection;

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

        var serviceProvider = BuildServiceProvider(dbOptions, configServiceMock);

        var config = new TestConnectorConfig
        {
            Enabled = true,
            SyncIntervalMinutes = 5
        };

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            config,
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
}
