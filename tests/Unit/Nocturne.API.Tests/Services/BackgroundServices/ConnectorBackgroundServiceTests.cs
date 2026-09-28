using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class ConnectorBackgroundServiceTests
{
    /// <summary>
    /// Minimal IConnectorConfiguration implementation for testing. The registration attribute is what
    /// names the connector in logs, health rows and audit endpoints.
    /// </summary>
    [ConnectorRegistration("TestConnector", "test-connector", "TESTCONNECTOR", "TestConnector")]
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
        private readonly TimeSpan? _unconfiguredRecheck;
        private readonly Func<DateTime, DateTime?>? _alignedSync;
        private readonly TimeSpan? _minAlignedSpacing;
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
            Action? onSyncCompleted = null,
            ConnectorSyncBudget? budget = null,
            ConnectorPollerNudge? nudge = null,
            TimeSpan? unconfiguredRecheck = null,
            ConnectorSyncMetrics? metrics = null,
            TenantRunGuard? runGuard = null,
            Func<DateTime, DateTime?>? alignedSync = null,
            TimeSpan? minAlignedSpacing = null)
            : base(serviceProvider, budget ?? new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), logger, nudge, metrics, runGuard)
        {
            _syncResult = syncResult;
            _onSync = onSync;
            _onSyncScope = onSyncScope;
            _perTenantTimeout = perTenantTimeout;
            _hangFirstNCalls = hangFirstNCalls;
            _onSyncCompleted = onSyncCompleted;
            _unconfiguredRecheck = unconfiguredRecheck;
            _alignedSync = alignedSync;
            _minAlignedSpacing = minAlignedSpacing;
        }

        protected override TimeSpan PerTenantSyncTimeout => _perTenantTimeout ?? base.PerTenantSyncTimeout;

        protected override TimeSpan UnconfiguredRecheckInterval => _unconfiguredRecheck ?? base.UnconfiguredRecheckInterval;

        protected override TimeSpan MinimumAlignedSyncSpacing => _minAlignedSpacing ?? base.MinimumAlignedSyncSpacing;

        protected override Task<DateTime?> GetAlignedSyncTimeAsync(
            IServiceProvider scopeProvider,
            TestConnectorConfig config,
            DateTime now,
            CancellationToken cancellationToken) =>
            _alignedSync is null
                ? base.GetAlignedSyncTimeAsync(scopeProvider, config, now, cancellationToken)
                : Task.FromResult(_alignedSync(now));

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

            cancellationToken.ThrowIfCancellationRequested();
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

    private static IServiceProvider BuildServiceProvider(
        SqliteTestDatabase db,
        Mock<IConnectorConfigurationService> configServiceMock,
        TestConnectorConfig config,
        Action? onConfigLoad = null,
        IConnectorTokenCache? tokenCache = null)
    {
        var services = new ServiceCollection();

        // Registered unconditionally, as production does: a harness that leaves it out would let
        // every test that does not pass one run down a path production never takes.
        services.AddSingleton(tokenCache ?? new ConnectorTokenCache());

        db.AddToServices(services);
        services.AddActiveTenantSnapshot();

        // Register scoped services
        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = MockTenantAccessor.Create(Guid.NewGuid());
            mock.Setup(t => t.SetTenant(It.IsAny<TenantContext>()));
            return mock.Object;
        });

        services.AddScoped<IConnectorConfigurationService>(_ => configServiceMock.Object);

        // Mirrors the production registration (Program.cs) — the sync scope resolves
        // the mutable scoped AuditContext to mark it system for the sync's duration.
        services.AddScoped<IAuditContext, AuditContext>();

        // Register config loader that returns the test config
        services.AddScoped<IConnectorConfigurationLoader<TestConnectorConfig>>(
            _ => new TestConfigLoader(config, onConfigLoad));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FailedSync_WithErrors_PropagatesErrorMessagesToHealthState()
    {
        // Arrange
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

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

        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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

    /// <summary>
    /// A repeated error must reach the health message once, and two errors that differ only in case
    /// are different errors; see <c>ConnectorConfigurationEntity.LastErrorMessageMaxLength</c>.
    /// </summary>
    [Fact]
    public async Task FailedSync_WithRepeatedErrors_JoinsEachDistinctMessageOnceCaseSensitively()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var syncResult = new SyncResult
        {
            Success = false,
            Message = "Fallback message",
            Errors =
            [
                .. Enumerable.Repeat("StateSpans publish failed", 20),
                .. Enumerable.Repeat("statespans publish failed", 20),
            ]
        };

        var configServiceMock = BuildEnabledConfigMock();
        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance);

        await sut.ExecuteOnceAsync(CancellationToken.None);

        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector",
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                "StateSpans publish failed; statespans publish failed",
                It.IsAny<DateTime?>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "identical chunk errors collapse to one entry, but a case difference is a different error");
    }

    [Fact]
    public async Task FailedSync_WithNoErrors_FallsBackToMessage()
    {
        // Arrange
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

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

        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

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

        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

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

        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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
    /// A sync must leave one duration with the outcome and one slot-wait measurement, tagged with the
    /// connector and never the tenant.
    /// </summary>
    [Fact]
    public async Task SuccessfulSync_RecordsDurationAndSlotWait()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        using var factory = new TestMeterFactory();
        using var listener = new ConnectorMetricListener(factory);
        var budget = new ConnectorSyncBudget();
        var metrics = new ConnectorSyncMetrics(factory, budget);

        var configServiceMock = BuildEnabledConfigMock();
        var serviceProvider = BuildServiceProvider(
            db, configServiceMock, new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 });

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true, Message = "OK" },
            NullLogger<TestConnectorBackgroundService>.Instance,
            budget: budget,
            metrics: metrics);

        await sut.ExecuteOnceAsync(CancellationToken.None);

        listener.SlotWaits.Should().ContainSingle("a sync takes exactly one slot");
        listener.SlotWaits[0].Connector.Should().Be("TestConnector");

        listener.Durations.Should().ContainSingle("a sync is measured exactly once");
        listener.Durations[0].Connector.Should().Be("TestConnector");
        listener.Durations[0].Outcome.Should().Be("success");
    }

    [Fact]
    public async Task FailedSync_RecordsFailureOutcome()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        using var factory = new TestMeterFactory();
        using var listener = new ConnectorMetricListener(factory);
        var budget = new ConnectorSyncBudget();
        var metrics = new ConnectorSyncMetrics(factory, budget);

        var configServiceMock = BuildEnabledConfigMock();
        var serviceProvider = BuildServiceProvider(
            db, configServiceMock, new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 });

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = false, Errors = ["upstream refused"] },
            NullLogger<TestConnectorBackgroundService>.Instance,
            budget: budget,
            metrics: metrics);

        await sut.ExecuteOnceAsync(CancellationToken.None);

        listener.Durations.Should().ContainSingle();
        listener.Durations[0].Outcome.Should().Be("failure");
    }

    /// <summary>
    ///     A connector that could not sign in has no token, so it fetches nothing and reports a run
    ///     that found no data — indistinguishable from a healthy source with nothing new. What the
    ///     token provider recorded about the sign-in is what has to override that.
    /// </summary>
    [Fact]
    public async Task FailedSignIn_MarksTheConnectorUnhealthy_EvenWhenTheRunReportedSuccess()
    {
        var tenantId = Guid.NewGuid();
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant");

        const string refusal = "TestConnector did not accept this sign-in.";
        var tokenCache = new ConnectorTokenCache();
        tokenCache.SetSignInFailure("TestConnector", tenantId, refusal);

        var configServiceMock = HealthRecordingConfigService();
        var serviceProvider = BuildServiceProvider(
            db,
            configServiceMock,
            new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 },
            tokenCache: tokenCache);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true, Message = "OK" },
            NullLogger<TestConnectorBackgroundService>.Instance);

        await sut.ExecuteOnceAsync(CancellationToken.None);

        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector",
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                refusal,
                It.IsAny<DateTime?>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a run that never signed in is not a healthy sync");
    }

    /// <summary>
    ///     A transient failure records nothing, so a run that carried on regardless stays healthy.
    /// </summary>
    [Fact]
    public async Task NoSignInFailure_LeavesASuccessfulRunHealthy()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var configServiceMock = HealthRecordingConfigService();
        var serviceProvider = BuildServiceProvider(
            db,
            configServiceMock,
            new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 },
            tokenCache: new ConnectorTokenCache());

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true, Message = "OK" },
            NullLogger<TestConnectorBackgroundService>.Instance);

        await sut.ExecuteOnceAsync(CancellationToken.None);

        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector",
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                string.Empty,
                It.IsAny<DateTime?>(),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    ///     One tenant's failed sign-in says nothing about another's, and the two share both the cache
    ///     and the connector name that keys it.
    /// </summary>
    [Fact]
    public async Task FailedSignIn_ForOneTenant_LeavesTheOtherTenantHealthy()
    {
        Guid[] tenantIds = [Guid.NewGuid(), Guid.NewGuid()];
        using var db = TestDbContextFactory.CreateSqlite()
            .SeedTenant(tenantIds[0], "tenant-a")
            .SeedTenant(tenantIds[1], "tenant-b");

        const string failure = "Could not sign in to TestConnector.";
        var tokenCache = new ConnectorTokenCache();
        tokenCache.SetSignInFailure("TestConnector", tenantIds[0], failure);

        var configServiceMock = HealthRecordingConfigService();
        var serviceProvider = BuildServiceProvider(
            db,
            configServiceMock,
            new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 },
            tokenCache: tokenCache);

        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true, Message = "OK" },
            NullLogger<TestConnectorBackgroundService>.Instance);

        await sut.ExecuteOnceAsync(CancellationToken.None);

        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector", It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                failure, It.IsAny<DateTime?>(), false, It.IsAny<CancellationToken>()),
            Times.Once,
            "the tenant whose sign-in failed");

        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector", It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                string.Empty, It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()),
            Times.Once,
            "the other tenant, whose sign-in was never in question");
    }

    /// <summary>Answers the reads the sync path makes and accepts every health write.</summary>
    private static Mock<IConnectorConfigurationService> HealthRecordingConfigService()
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
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    [Fact]
    public async Task SyncForTenant_PinsScopedDbContextToSyncedTenant_ForRlsIsolation()
    {
        // Regression test for the connector-wide outage: a NocturneDbContext's TenantId defaults
        // to Guid.Empty. The background sync must set dbContext.TenantId to the tenant being
        // synced — otherwise the
        // TenantConnectionInterceptor applies a stale/empty RLS tenant, tenant-scoped reads
        // (connector config + secrets) silently return nothing, and every connector authenticates
        // with empty credentials. Before the fix the scoped context stayed at Guid.Empty here.
        var tenantId = Guid.NewGuid();
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant");

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
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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
    public async Task SyncForTenant_SystemAttributesTheScopeToTheConnector()
    {
        // Regression test for the mutation_audit_log firehose: V4 repositories stamp their
        // factory-created DbContexts from the scoped IAuditContext (V4RepositoryBase), not from
        // the scoped NocturneDbContext the sync annotates. A background scope's AuditContext is
        // a blank user context (IsSystem = false), so every connector upsert was audited with
        // null attribution — ~1.5M rows/day in production — instead of being skipped as a
        // system mutation.
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

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
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

        bool? capturedIsSystem = null;
        string? capturedEndpoint = null;
        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            syncResult,
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSyncScope: sp =>
            {
                capturedIsSystem = sp.GetRequiredService<IAuditContext>().IsSystem;
                capturedEndpoint = sp.GetRequiredService<NocturneDbContext>().AuditContext?.Endpoint;
            });

        // Act
        await sut.ExecuteOnceAsync(CancellationToken.None);

        // Assert — everything written during the sync must carry system attribution.
        Assert.True(capturedIsSystem);
        Assert.Equal("connector:testconnector", capturedEndpoint);
    }

    [Fact]
    public async Task SyncAllTenants_RunsTenantsConcurrently_OneStuckTenantDoesNotBlockOthers()
    {
        // Tenants must sync independently: a tenant whose sync hangs (e.g. an auth-retry storm against
        // bad credentials) must not delay or block any other tenant of the connector.
        using var db = TestDbContextFactory.CreateSqlite()
            .SeedTenant(Guid.NewGuid(), "tenant-a")
            .SeedTenant(Guid.NewGuid(), "tenant-b");

        var configServiceMock = BuildEnabledConfigMock();
        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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

    /// <summary>
    /// The budget is process-wide: a tenant sync in one poller holds a slot that a tenant sync in
    /// another poller has to wait for. Per-poller caps alone let the total climb with the connector
    /// count, which is what exhausted Postgres connections in production. The slot gates the tenant's
    /// DI scope and config load, not just the sync proper — the config load is the connection the
    /// unconfigured majority of tenants open.
    /// </summary>
    [Fact]
    public async Task SyncAllTenants_SharesTheBudgetAcrossPollers_ASecondPollerWaitsForASlot()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var configServiceMock = BuildEnabledConfigMock();
        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };

        var budget = new ConnectorSyncBudget(slots: 1);
        using var firstCts = new CancellationTokenSource();

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestConnectorBackgroundService(
            BuildServiceProvider(db, configServiceMock, config),
            new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSync: () => firstStarted.TrySetResult(),
            perTenantTimeout: TimeSpan.FromSeconds(30),
            hangFirstNCalls: 1,
            budget: budget);

        var secondConfigLoads = 0;
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TestConnectorBackgroundService(
            BuildServiceProvider(db, configServiceMock, config,
                onConfigLoad: () => Interlocked.Increment(ref secondConfigLoads)),
            new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSyncCompleted: () => secondDone.TrySetResult(),
            budget: budget);

        var firstRun = first.ExecuteOnceAsync(firstCts.Token);
        try
        {
            (await Task.WhenAny(firstStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(firstStarted.Task, "the first poller must take the only slot");

            var secondRun = second.ExecuteOnceAsync(CancellationToken.None);

            (await Task.WhenAny(secondDone.Task, Task.Delay(TimeSpan.FromMilliseconds(500))))
                .Should().NotBe(secondDone.Task,
                    "the second poller's tenant must wait while the first poller holds the slot");
            secondConfigLoads.Should().Be(0, "the slot must be held before the tenant's scope loads config");
            budget.InFlight.Should().Be(1);

            firstCts.Cancel();

            (await Task.WhenAny(secondDone.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(secondDone.Task, "the slot the first poller released must go to the second");
            await secondRun;
            secondConfigLoads.Should().Be(1);
        }
        finally
        {
            firstCts.Cancel();
            try { await firstRun; } catch (OperationCanceledException) { }
        }

        budget.InFlight.Should().Be(0, "every lease must be returned once the syncs are over");
    }

    /// <summary>
    /// Queueing for a slot is not the tenant's time: its <c>PerTenantSyncTimeout</c> starts once the
    /// slot is held, so a tenant that waited longer than the timeout still gets its full sync.
    /// </summary>
    [Fact]
    public async Task SyncAllTenants_StartsThePerTenantTimeout_OnlyOnceASlotIsHeld()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var configServiceMock = BuildEnabledConfigMock();
        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

        var budget = new ConnectorSyncBudget(slots: 1);
        using var firstCts = new CancellationTokenSource();

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSync: () => firstStarted.TrySetResult(),
            perTenantTimeout: TimeSpan.FromSeconds(30),
            hangFirstNCalls: 1,
            budget: budget);

        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            onSyncCompleted: () => secondDone.TrySetResult(),
            perTenantTimeout: TimeSpan.FromMilliseconds(200),
            budget: budget);

        var firstRun = first.ExecuteOnceAsync(firstCts.Token);
        try
        {
            (await Task.WhenAny(firstStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(firstStarted.Task, "the first poller must take the only slot");

            var secondRun = second.ExecuteOnceAsync(CancellationToken.None);

            // Hold the slot for well over the second poller's timeout before releasing it.
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            firstCts.Cancel();

            (await Task.WhenAny(secondDone.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(secondDone.Task,
                    "a tenant that queued longer than its timeout must still run once it holds a slot");
            await secondRun;
        }
        finally
        {
            firstCts.Cancel();
            try { await firstRun; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// A poller's first tick waits its phase offset on top of <c>StartupDelay</c>, so pollers that
    /// start together do not tick together. <see cref="ExecuteAsync_RunsListenerSupervisionFromThePollLoop"/>
    /// is the control: the first poller on a budget has no offset and ticks at once.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WaitsThePollersStaggerOffsetBeforeItsFirstTick()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var serviceProvider = BuildServiceProvider(
            db,
            BuildEnabledConfigMock(),
            new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 });

        var budget = new ConnectorSyncBudget(pollerCount: 2);
        budget.NextStartupOffset(TimeSpan.FromHours(1));

        var sut = new PollLoopWiringService(serviceProvider, budget, pollInterval: TimeSpan.FromHours(1));
        await sut.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            sut.Events.Should().BeEmpty("the second of two pollers waits half the poll interval before its first tick");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SyncForTenant_IsCancelled_WhenItExceedsPerTenantTimeout()
    {
        // A stuck tenant must be cancelled at PerTenantSyncTimeout so it cannot hold a slot forever.
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var configServiceMock = BuildEnabledConfigMock();
        var config = new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 };
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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

    [Fact]
    public async Task SyncForTenant_WhenASyncThrowsACancellationNobodyAskedFor_LogsItAsAnErrorNotATimeout()
    {
        // A cancellation of neither the poller's token nor the timeout's is not the per-tenant
        // timeout, so it must fall through to the generic handler rather than be reported as one.
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var serviceProvider = BuildServiceProvider(
            db, BuildEnabledConfigMock(), new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 });

        var logger = new MessageRecordingLogger();
        var sut = new TestConnectorBackgroundService(
            serviceProvider,
            new SyncResult { Success = true },
            logger,
            onSync: () => throw new OperationCanceledException());

        await sut.ExecuteOnceAsync(CancellationToken.None);

        logger.Messages.Should().NotContain(m => m.Contains("exceeded"));
        logger.Messages.Should().Contain(m => m.Contains("Error syncing"));
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
        var tenantId = Guid.NewGuid();
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant");

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
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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
        var tenantId = Guid.NewGuid();
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant");

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
        var serviceProvider = BuildServiceProvider(db, configServiceMock, config);

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
    /// Almost every tenant has no configuration for almost every connector. A tenant found
    /// unconfigured is left alone until <see cref="ConnectorBackgroundService{TConfig}.UnconfiguredRecheckInterval"/>
    /// rather than having its absent row read on every tick.
    /// </summary>
    [Fact]
    public async Task SyncAllTenants_ForAnUnconfiguredTenant_ReadsConfigOnceUntilTheRecheckInterval()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var configLoads = 0;
        var serviceProvider = BuildServiceProvider(
            db, BuildEnabledConfigMock(), new TestConnectorConfig { Enabled = false },
            onConfigLoad: () => Interlocked.Increment(ref configLoads));

        var sut = new TestConnectorBackgroundService(
            serviceProvider, new SyncResult { Success = true }, NullLogger<TestConnectorBackgroundService>.Instance,
            unconfiguredRecheck: TimeSpan.FromHours(1));

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        configLoads.Should().Be(1, "an unconfigured tenant is not asked again inside the recheck interval");
        sut.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SyncAllTenants_ForAnUnconfiguredTenant_ReadsConfigAgainOnceTheRecheckIntervalHasPassed()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var configLoads = 0;
        var serviceProvider = BuildServiceProvider(
            db, BuildEnabledConfigMock(), new TestConnectorConfig { Enabled = false },
            onConfigLoad: () => Interlocked.Increment(ref configLoads));

        var sut = new TestConnectorBackgroundService(
            serviceProvider, new SyncResult { Success = true }, NullLogger<TestConnectorBackgroundService>.Instance,
            unconfiguredRecheck: TimeSpan.Zero);

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        configLoads.Should().Be(2);
    }

    /// <summary>
    /// A configured tenant inside its interval is skipped until the interval has elapsed; the tick
    /// does not read its configuration to learn it is not due.
    /// </summary>
    [Fact]
    public async Task SyncAllTenants_ForAConfiguredTenantInsideItsInterval_DoesNotReadConfig()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var configLoads = 0;
        var serviceProvider = BuildServiceProvider(
            db, BuildEnabledConfigMock(), new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 60 },
            onConfigLoad: () => Interlocked.Increment(ref configLoads));

        var sut = new TestConnectorBackgroundService(
            serviceProvider, new SyncResult { Success = true }, NullLogger<TestConnectorBackgroundService>.Instance);

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        sut.CallCount.Should().Be(1);
        configLoads.Should().Be(1, "the interval is known from the first read; the next reads wait for it to elapse");
    }

    private static TestConnectorBackgroundService AlignedPoller(
        SqliteTestDatabase db,
        Func<DateTime, DateTime?> alignedSync,
        SyncResult? syncResult = null,
        TimeSpan? minAlignedSpacing = null) =>
        new(
            BuildServiceProvider(
                db, BuildEnabledConfigMock(), new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 60 }),
            syncResult ?? new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            alignedSync: alignedSync,
            minAlignedSpacing: minAlignedSpacing);

    /// <summary>
    /// A connector that knows when its source next has data brings the sync forward to then: the
    /// interval is the longest the poller waits, not the shortest. The aligned time is spent by the
    /// sync it scheduled, so a later tick is back under the interval.
    /// </summary>
    [Fact]
    public async Task AlignedSyncTime_OnceArrived_SyncsInsideTheInterval_AndIsSpentByThatSync()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var answers = new Queue<DateTime?>();
        var sut = AlignedPoller(
            db,
            now => answers.Count > 0 ? answers.Dequeue() : now,
            minAlignedSpacing: TimeSpan.Zero);

        // First sync aligns the next one to "now", which has arrived by the next tick.
        await sut.ExecuteOnceAsync(CancellationToken.None);
        sut.CallCount.Should().Be(1);

        answers.Enqueue(null); // the aligned sync reports no further alignment
        await sut.ExecuteOnceAsync(CancellationToken.None);
        sut.CallCount.Should().Be(2, "the aligned time had arrived, although the 60-minute interval had not");

        await sut.ExecuteOnceAsync(CancellationToken.None);
        sut.CallCount.Should().Be(2, "the alignment was spent by the sync it scheduled; the interval stands again");
    }

    [Fact]
    public async Task AlignedSyncTime_NotYetArrived_WaitsForIt()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var sut = AlignedPoller(db, now => now.AddMinutes(10), minAlignedSpacing: TimeSpan.Zero);

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        sut.CallCount.Should().Be(1);
    }

    /// <summary>
    /// A connector that answers "now" after every sync would otherwise be synced on every tick.
    /// </summary>
    [Fact]
    public async Task AlignedSyncTime_IsNeverSooner_ThanTheMinimumSpacing()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var sut = AlignedPoller(db, now => now, minAlignedSpacing: TimeSpan.FromMinutes(5));

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        sut.CallCount.Should().Be(1);
    }

    /// <summary>
    /// Only a sync that succeeded says anything about the source's cadence; a failing source is left
    /// on the plain interval rather than retried at the aligned time.
    /// </summary>
    [Fact]
    public async Task AFailedSync_IsNotAligned()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var asked = 0;
        var sut = AlignedPoller(
            db,
            now => { asked++; return now; },
            syncResult: new SyncResult { Success = false, Message = "down" },
            minAlignedSpacing: TimeSpan.Zero);

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        sut.CallCount.Should().Be(1);
        asked.Should().Be(0);
    }

    /// <summary>
    /// The alignment is an optimisation over a sync that already succeeded: when working it out
    /// fails, the sync is still reported healthy and the interval still applies.
    /// </summary>
    [Fact]
    public async Task AnAlignmentThatThrows_LeavesTheSyncHealthyAndTheIntervalInCharge()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");
        var configServiceMock = BuildEnabledConfigMock();
        var sut = new TestConnectorBackgroundService(
            BuildServiceProvider(
                db, configServiceMock, new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 60 }),
            new SyncResult { Success = true },
            NullLogger<TestConnectorBackgroundService>.Instance,
            alignedSync: _ => throw new InvalidOperationException("no readings table"),
            minAlignedSpacing: TimeSpan.Zero);

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        sut.CallCount.Should().Be(1);
        configServiceMock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector", It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<DateTime?>(), true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The schedule must never hide a configuration the tenant just saved: the configuration
    /// service's cache-invalidation hook reaches the poller through <see cref="ConnectorPollerNudge"/>
    /// and clears the tenant's next-check, so the next tick reads and syncs.
    /// </summary>
    [Fact]
    public async Task SyncAllTenants_AfterAConfigurationWriteNudge_ReadsTheUnconfiguredTenantAgain()
    {
        var tenantId = Guid.NewGuid();
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant");
        var configLoads = 0;
        var serviceProvider = BuildServiceProvider(
            db, BuildEnabledConfigMock(), new TestConnectorConfig { Enabled = false },
            onConfigLoad: () => Interlocked.Increment(ref configLoads));
        var nudge = new ConnectorPollerNudge();

        var sut = new TestConnectorBackgroundService(
            serviceProvider, new SyncResult { Success = true }, NullLogger<TestConnectorBackgroundService>.Instance,
            nudge: nudge, unconfiguredRecheck: TimeSpan.FromHours(1));

        await sut.ExecuteOnceAsync(CancellationToken.None);
        await sut.ExecuteOnceAsync(CancellationToken.None);
        configLoads.Should().Be(1);

        // The configuration service names the connector as its row does, lower-case.
        nudge.Invalidate("testconnector", tenantId);
        await sut.ExecuteOnceAsync(CancellationToken.None);

        configLoads.Should().Be(2, "a configuration write must be seen on the next tick, not after the recheck interval");
    }

    /// <summary>
    /// A listener that has died must be evicted from the tracking dictionary and disposed so the next
    /// supervision pass can replace it, and the loss of real-time delivery must be visible in the log.
    /// </summary>
    [Fact]
    public async Task ListenerNeedsStart_DeadClient_EvictsDisposesAndLogs()
    {
        var logger = new MessageRecordingLogger();
        var sut = new SupervisedListenerService(logger, TimeSpan.Zero);
        var tenantId = Guid.NewGuid();
        var client = new FakeListenerClient { IsAlive = false };
        sut.Clients[tenantId] = client;

        var needsStart = await sut.ListenerNeedsStartAsync(tenantId);

        Assert.True(needsStart);
        Assert.False(sut.Clients.ContainsKey(tenantId));
        Assert.Equal(1, client.DisposeCount);
        Assert.Contains(logger.Messages, m => m.Contains("no longer connected"));
    }

    /// <summary>
    /// A tenant whose listener is still connected must be left exactly as it is — no eviction, no
    /// dispose, and no second client registered on top of it.
    /// </summary>
    [Fact]
    public async Task ListenerNeedsStart_LiveClient_LeavesItRegistered()
    {
        var logger = new MessageRecordingLogger();
        var sut = new SupervisedListenerService(logger, TimeSpan.Zero);
        var tenantId = Guid.NewGuid();
        var client = new FakeListenerClient { IsAlive = true };
        sut.Clients[tenantId] = client;

        var needsStart = await sut.ListenerNeedsStartAsync(tenantId);

        Assert.False(needsStart);
        Assert.Same(client, sut.Clients[tenantId]);
        Assert.Equal(0, client.DisposeCount);
        Assert.Empty(logger.Messages);
    }

    /// <summary>
    /// A tenant with no tracked listener at all — never started, or evicted by an earlier pass — needs one.
    /// </summary>
    [Fact]
    public async Task ListenerNeedsStart_NoTrackedClient_RequestsStart()
    {
        var sut = new SupervisedListenerService(NullLogger.Instance, TimeSpan.Zero);

        Assert.True(await sut.ListenerNeedsStartAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// Once the supervision interval has elapsed the poll loop re-runs listener startup, which is what
    /// replaces a socket that exhausted its reconnection budget.
    /// </summary>
    [Fact]
    public async Task SuperviseRealtimeListeners_AfterInterval_RunsListenerStartupAgain()
    {
        var sut = new SupervisedListenerService(NullLogger.Instance, TimeSpan.Zero);

        await sut.SuperviseOnceAsync(CancellationToken.None);
        await sut.SuperviseOnceAsync(CancellationToken.None);

        Assert.Equal(2, sut.StartCount);
    }

    /// <summary>
    /// Supervision is coarser than the one-minute poll tick, so a permanently unreachable upstream is
    /// not reconnected on every cycle.
    /// </summary>
    [Fact]
    public async Task SuperviseRealtimeListeners_WithinInterval_DoesNotRunListenerStartupAgain()
    {
        var sut = new SupervisedListenerService(NullLogger.Instance, TimeSpan.FromMinutes(5));

        await sut.SuperviseOnceAsync(CancellationToken.None);
        await sut.SuperviseOnceAsync(CancellationToken.None);

        Assert.Equal(1, sut.StartCount);
    }

    /// <summary>
    /// The poll loop itself must run listener supervision: before the first sync cycle at startup,
    /// and again on later ticks — that repeat is what replaces a listener that dies mid-lifetime.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RunsListenerSupervisionFromThePollLoop()
    {
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(Guid.NewGuid(), "test-tenant");

        var serviceProvider = BuildServiceProvider(
            db,
            BuildEnabledConfigMock(),
            new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 });

        var sut = new PollLoopWiringService(serviceProvider);
        await sut.StartAsync(CancellationToken.None);
        try
        {
            var winner = await Task.WhenAny(sut.SecondSupervisionPass, Task.Delay(TimeSpan.FromSeconds(10)));
            winner.Should().Be(sut.SecondSupervisionPass,
                "the poll loop must re-run listener supervision on later ticks, not just once before the loop");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        sut.Events.TryPeek(out var first);
        first.Should().Be("listeners", "listener startup must precede the first sync cycle");
    }

    /// <summary>
    /// Runs the real ExecuteAsync poll loop with test-fast intervals, recording listener-startup
    /// passes and sync cycles in order.
    /// </summary>
    private sealed class PollLoopWiringService(
        IServiceProvider serviceProvider,
        ConnectorSyncBudget? budget = null,
        TimeSpan? pollInterval = null)
        : ConnectorBackgroundService<TestConnectorConfig>(serviceProvider, budget ?? new ConnectorSyncBudget(), serviceProvider.GetRequiredService<ActiveTenantSnapshot>(), NullLogger.Instance)
    {
        private readonly TaskCompletionSource _secondSupervisionPass =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _listenerStartCount;

        public ConcurrentQueue<string> Events { get; } = new();

        public Task SecondSupervisionPass => _secondSupervisionPass.Task;

        protected override TimeSpan StartupDelay => TimeSpan.Zero;

        protected override TimeSpan PollInterval => pollInterval ?? TimeSpan.FromMilliseconds(20);

        protected override TimeSpan RealtimeSupervisionInterval => TimeSpan.Zero;

        protected override Task StartRealtimeListenersAsync(CancellationToken cancellationToken)
        {
            Events.Enqueue("listeners");
            if (Interlocked.Increment(ref _listenerStartCount) >= 2)
                _secondSupervisionPass.TrySetResult();
            return Task.CompletedTask;
        }

        protected override Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            TestConnectorConfig config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null)
        {
            Events.Enqueue("sync");
            return Task.FromResult(new SyncResult { Success = true });
        }
    }

    /// <summary>
    /// A manual sync holds the same (tenant, connector) key the poller uses, so the cycle must
    /// skip that tenant rather than queue behind it.
    /// </summary>
    [Fact]
    public async Task SyncAllTenants_WhenASyncIsAlreadyRunning_SkipsThatTenantWithoutWaiting()
    {
        var tenantId = Guid.NewGuid();
        using var db = TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant");

        var guard = new TenantRunGuard();
        using var held = guard.TryAcquire(tenantId, "testconnector");
        held.Should().NotBeNull();

        var logger = new MessageRecordingLogger();
        var sut = new TestConnectorBackgroundService(
            BuildServiceProvider(
                db, BuildEnabledConfigMock(), new TestConnectorConfig { Enabled = true, SyncIntervalMinutes = 5 }),
            new SyncResult { Success = true },
            logger,
            runGuard: guard);

        await sut.ExecuteOnceAsync(CancellationToken.None);

        sut.CallCount.Should().Be(0, "a held key makes the cycle skip the tenant, not run it");
        logger.Messages.Should().Contain(m => m.Contains("skipped"));
    }

    /// <summary>
    /// Stand-in for a real-time client whose liveness the test controls.
    /// </summary>
    private sealed class FakeListenerClient
    {
        public bool IsAlive { get; init; }

        public int DisposeCount { get; private set; }

        public Task DisposeAsync()
        {
            DisposeCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Exposes the base class's real-time supervision hooks and counts listener-startup passes.
    /// </summary>
    private sealed class SupervisedListenerService(ILogger logger, TimeSpan supervisionInterval)
        : ConnectorBackgroundService<TestConnectorConfig>(
            new ServiceCollection().BuildServiceProvider(),
            new ConnectorSyncBudget(),
            ActiveTenantSnapshotTestDoubles.Unread(),
            logger)
    {
        private int _startCount;

        public ConcurrentDictionary<Guid, FakeListenerClient> Clients { get; } = new();

        public int StartCount => _startCount;

        protected override TimeSpan RealtimeSupervisionInterval => supervisionInterval;

        protected override Task StartRealtimeListenersAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            return Task.CompletedTask;
        }

        protected override Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            TestConnectorConfig config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null)
            => throw new NotSupportedException();

        public Task<bool> ListenerNeedsStartAsync(Guid tenantId)
            => ListenerNeedsStartAsync(Clients, tenantId, "test-tenant", c => c.IsAlive, c => c.DisposeAsync());

        /// <summary>
        /// Invokes the private supervision pass the poll loop runs each tick, via reflection.
        /// </summary>
        public async Task SuperviseOnceAsync(CancellationToken ct)
        {
            var method = typeof(ConnectorBackgroundService<TestConnectorConfig>)
                .GetMethod("SuperviseRealtimeListenersAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)method.Invoke(this, [ct])!;
        }
    }

    /// <summary>
    /// Records formatted log messages so tests can assert on what was reported.
    /// </summary>
    private sealed class MessageRecordingLogger : ILogger
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToList(); }
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
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// Concrete config loader that returns a preconfigured TestConnectorConfig.
    /// </summary>
    private sealed class TestConfigLoader(TestConnectorConfig config, Action? onLoad = null)
        : IConnectorConfigurationLoader<TestConnectorConfig>
    {
        public Task<TestConnectorConfig> LoadForTenantAsync(CancellationToken ct)
        {
            onLoad?.Invoke();
            return Task.FromResult(config);
        }
    }
}
