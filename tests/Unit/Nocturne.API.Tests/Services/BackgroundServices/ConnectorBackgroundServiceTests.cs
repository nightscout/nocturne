using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// One run of a connector poller for one tenant, called as <c>JobScheduler</c> calls it: in a scope
/// opened for the row's tenant.
/// </summary>
public class ConnectorBackgroundServiceTests
{
    private const string JobName = "connector:testconnector";

    /// <summary>On the minute, so <c>Start + phase</c> is a point of the tenant's phase.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Minimal IConnectorConfiguration implementation for testing. The registration attribute is what
    /// names the connector in logs, health rows and the job name.
    /// </summary>
    [ConnectorRegistration("TestConnector", "test-connector", "TESTCONNECTOR", "TestConnector")]
    private class TestConnectorConfig : BaseConnectorConfiguration;

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
        private readonly Func<DateTime, DateTime?>? _alignedSync;
        private readonly TimeSpan? _minAlignedSpacing;
        private readonly int _hangFirstNCalls;
        private int _callCount;

        public TestConnectorBackgroundService(
            SyncResult syncResult,
            ILogger logger,
            Action? onSync = null,
            Action<IServiceProvider>? onSyncScope = null,
            TimeSpan? perTenantTimeout = null,
            int hangFirstNCalls = 0,
            Action? onSyncCompleted = null,
            ConnectorSyncBudget? budget = null,
            ConnectorPollerNudge? nudge = null,
            ConnectorSyncMetrics? metrics = null,
            TenantRunGuard? runGuard = null,
            Func<DateTime, DateTime?>? alignedSync = null,
            TimeSpan? minAlignedSpacing = null,
            IJobLedger? ledger = null,
            TimeProvider? timeProvider = null)
            : base(budget ?? new ConnectorSyncBudget(), ledger ?? Mock.Of<IJobLedger>(), logger, timeProvider ?? TimeProvider.System, nudge, metrics, runGuard)
        {
            _syncResult = syncResult;
            _onSync = onSync;
            _onSyncScope = onSyncScope;
            _perTenantTimeout = perTenantTimeout;
            _hangFirstNCalls = hangFirstNCalls;
            _onSyncCompleted = onSyncCompleted;
            _alignedSync = alignedSync;
            _minAlignedSpacing = minAlignedSpacing;
        }

        protected override TimeSpan PerTenantSyncTimeout => _perTenantTimeout ?? base.PerTenantSyncTimeout;

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

        public new void RequestImmediateSync(Guid tenantId) => base.RequestImmediateSync(tenantId);
    }

    /// <summary>
    /// Runs <paramref name="sut"/> once for the tenant, in a scope opened as the scheduler opens one,
    /// by default at the tenant's phase after <see cref="Start"/>.
    /// </summary>
    private static async Task<JobOutcome> RunAsync(
        TestConnectorBackgroundService sut,
        IServiceProvider root,
        Guid tenantId,
        CancellationToken ct = default,
        DateTimeOffset? startedAt = null,
        bool isActive = true,
        string slug = "test-tenant")
    {
        var tenant = new TenantContext(tenantId, slug, "Test", isActive, false);
        await using var scope = TenantJobScope.Open(root, tenant, $"job:{sut.Name}");
        return await sut.RunAsync(new JobRun(tenant, null, 0, scope.ServiceProvider, startedAt ?? Start + PhaseOf(tenantId)), ct);
    }

    private static DateTimeOffset DueAt(JobOutcome outcome) =>
        outcome.Should().BeOfType<JobOutcome.Resumed>().Subject.NextDueAt;

    private static TimeSpan PhaseOf(Guid tenantId) => JobPhase.Of(JobName, tenantId, TimeSpan.FromMinutes(1));

    private static IServiceProvider BuildServiceProvider(
        SqliteTestDatabase db,
        Mock<IConnectorConfigurationService> configServiceMock,
        TestConnectorConfig config,
        Action? onConfigLoad = null,
        IConnectorTokenCache? tokenCache = null,
        Exception? configLoadFailure = null)
    {
        var services = new ServiceCollection();

        // Registered unconditionally, as production does: a harness that leaves it out would let
        // every test that does not pass one run down a path production never takes.
        services.AddSingleton(tokenCache ?? new ConnectorTokenCache());

        db.AddToServices(services);

        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped(sp => new TenantReadContext(
            sp.GetRequiredService<ITenantAccessor>(), null, sp.GetRequiredService<IAuditContext>()));

        services.AddScoped<IConnectorConfigurationService>(_ => configServiceMock.Object);
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<IConnectorConfigurationLoader<TestConnectorConfig>>(
            _ => new TestConfigLoader(config, onConfigLoad, configLoadFailure));

        return services.BuildServiceProvider();
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

    private static TestConnectorConfig Enabled(int intervalMinutes = 5) =>
        new() { Enabled = true, SyncIntervalMinutes = intervalMinutes };

    private static (Guid Tenant, SqliteTestDatabase Db) Tenant()
    {
        var tenantId = Guid.NewGuid();
        return (tenantId, TestDbContextFactory.CreateSqlite().SeedTenant(tenantId, "test-tenant"));
    }

    private static void VerifyHealth(
        Mock<IConnectorConfigurationService> mock, string? message, bool healthy, string because = "")
    {
        mock.Verify(
            x => x.UpdateHealthStateAsync(
                "TestConnector", It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                message, It.IsAny<DateTime?>(), healthy, It.IsAny<CancellationToken>()),
            Times.Once,
            because);
    }

    [Fact]
    public void TheJob_IsNamedAfterTheConnector_EnrolsEveryTenant_AndBacksOffNoLongerThanFifteenMinutes()
    {
        var sut = new TestConnectorBackgroundService(new SyncResult(), NullLogger.Instance);

        sut.Name.Should().Be(JobName, "the job name is the audit endpoint a manual sync is attributed under");
        sut.Enrolment.Should().Be(JobEnrolment.PerTenant);
        sut.MaxBackoff.Should().Be(TimeSpan.FromMinutes(15));
        sut.Spread.Should().Be(TimeSpan.FromMinutes(1));
        sut.MaxConcurrency.Should().Be(8, "a connector whose tenants are all stuck leaves slots to the other jobs");
    }

    [Fact]
    public async Task FailedSync_WithErrors_PropagatesErrorMessagesToHealthState()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configServiceMock = BuildEnabledConfigMock();
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = false, Message = "Fallback message", Errors = ["Connection refused", "Timeout after 30s"] },
            NullLogger.Instance);

        await RunAsync(sut, BuildServiceProvider(db, configServiceMock, Enabled()), tenant);

        VerifyHealth(configServiceMock, "Connection refused; Timeout after 30s", healthy: false,
            "the specific error messages from SyncResult.Errors reach the health state");
    }

    /// <summary>
    /// A repeated error must reach the health message once, and two errors that differ only in case
    /// are different errors; see <c>ConnectorConfigurationEntity.LastErrorMessageMaxLength</c>.
    /// </summary>
    [Fact]
    public async Task FailedSync_WithRepeatedErrors_JoinsEachDistinctMessageOnceCaseSensitively()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configServiceMock = BuildEnabledConfigMock();
        var sut = new TestConnectorBackgroundService(
            new SyncResult
            {
                Success = false,
                Message = "Fallback message",
                Errors =
                [
                    .. Enumerable.Repeat("StateSpans publish failed", 20),
                    .. Enumerable.Repeat("statespans publish failed", 20),
                ]
            },
            NullLogger.Instance);

        await RunAsync(sut, BuildServiceProvider(db, configServiceMock, Enabled()), tenant);

        VerifyHealth(configServiceMock, "StateSpans publish failed; statespans publish failed", healthy: false,
            "identical chunk errors collapse to one entry, but a case difference is a different error");
    }

    [Fact]
    public async Task FailedSync_WithNoErrors_FallsBackToMessage()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configServiceMock = BuildEnabledConfigMock();
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = false, Message = "Custom failure message", Errors = [] },
            NullLogger.Instance);

        await RunAsync(sut, BuildServiceProvider(db, configServiceMock, Enabled()), tenant);

        VerifyHealth(configServiceMock, "Custom failure message", healthy: false);
    }

    [Fact]
    public async Task FailedSync_WithNoErrorsAndNoMessage_FallsBackToDefault()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configServiceMock = BuildEnabledConfigMock();
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = false, Message = "", Errors = [] },
            NullLogger.Instance);

        await RunAsync(sut, BuildServiceProvider(db, configServiceMock, Enabled()), tenant);

        VerifyHealth(configServiceMock, "Sync failed", healthy: false);
    }

    /// <summary>
    /// A sync that ran and reported failure is not a failed run: the tenant's health says what went
    /// wrong and its interval stands, rather than the ledger's backoff.
    /// </summary>
    [Fact]
    public async Task FailedSync_ComesDueAgainAfterItsInterval()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = false, Message = "down" }, NullLogger.Instance);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);

        DueAt(outcome).Should().Be(Start + TimeSpan.FromMinutes(5) + PhaseOf(tenant));
    }

    [Fact]
    public async Task SuccessfulSync_ClearsErrorMessage_AndComesDueAfterItsIntervalAtTheTenantsPhase()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configServiceMock = BuildEnabledConfigMock();
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true, Message = "OK" }, NullLogger.Instance);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, configServiceMock, Enabled()), tenant);

        VerifyHealth(configServiceMock, string.Empty, healthy: true, "the error message is cleared on a successful sync");
        DueAt(outcome).Should().Be(Start + TimeSpan.FromMinutes(5) + PhaseOf(tenant));
    }

    /// <summary>
    /// A run that starts early, or up to two polls late, comes due on the tenant's phase one interval
    /// on, so the tenant keeps its place among the others.
    /// </summary>
    [Theory]
    [InlineData(-20)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public async Task ARunStartedNearItsPhase_ComesDueOnItOneIntervalOn(int secondsOff)
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true }, NullLogger.Instance);
        var onPhase = Start + PhaseOf(tenant);

        var outcome = await RunAsync(
            sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant,
            startedAt: onPhase + TimeSpan.FromSeconds(secondsOff));

        DueAt(outcome).Should().Be(onPhase + TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// A run well off its phase (one a listener's event asked for) comes due at the first phase point
    /// a whole interval, less two of the scheduler's polls, after it: never sooner.
    /// </summary>
    [Theory]
    [InlineData(29, 1, 120)]
    [InlineData(31, 1, 120)]
    [InlineData(25, 5, 360)]
    public async Task ARunStartedOffItsPhase_ComesDueNoSoonerThanItsInterval(int secondsOff, int intervalMinutes, int dueSecondsAfterPhase)
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true }, NullLogger.Instance);
        var onPhase = Start + PhaseOf(tenant);
        var startedAt = onPhase + TimeSpan.FromSeconds(secondsOff);

        var outcome = await RunAsync(
            sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled(intervalMinutes)), tenant, startedAt: startedAt);

        DueAt(outcome).Should().Be(onPhase + TimeSpan.FromSeconds(dueSecondsAfterPhase));
        (DueAt(outcome) - startedAt).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(intervalMinutes) - TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TwoTenants_HaveTheirOwnPhases()
    {
        var phases = Enumerable.Range(0, 20).Select(_ => PhaseOf(Guid.NewGuid())).ToList();

        phases.Should().OnlyContain(p => p >= TimeSpan.Zero && p < TimeSpan.FromMinutes(1));
        phases.Distinct().Should().HaveCountGreaterThan(1);
        PhaseOf(Guid.Parse("0190a3c4-0000-7000-8000-000000000001"))
            .Should().Be(PhaseOf(Guid.Parse("0190a3c4-0000-7000-8000-000000000001")), "a tenant keeps its phase");
    }

    /// <summary>
    /// A sync must leave one duration with the outcome and one slot-wait measurement, tagged with the
    /// connector and never the tenant.
    /// </summary>
    [Fact]
    public async Task SuccessfulSync_RecordsDurationAndSlotWait()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        using var factory = new TestMeterFactory();
        using var listener = new ConnectorMetricListener(factory);
        var budget = new ConnectorSyncBudget();
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = true, Message = "OK" },
            NullLogger.Instance,
            budget: budget,
            metrics: new ConnectorSyncMetrics(factory, budget));

        await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);

        listener.SlotWaits.Should().ContainSingle("a sync takes exactly one slot");
        listener.SlotWaits[0].Connector.Should().Be("TestConnector");
        listener.Durations.Should().ContainSingle("a sync is measured exactly once");
        listener.Durations[0].Connector.Should().Be("TestConnector");
        listener.Durations[0].Outcome.Should().Be("success");
    }

    [Fact]
    public async Task FailedSync_RecordsFailureOutcome()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        using var factory = new TestMeterFactory();
        using var listener = new ConnectorMetricListener(factory);
        var budget = new ConnectorSyncBudget();
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = false, Errors = ["upstream refused"] },
            NullLogger.Instance,
            budget: budget,
            metrics: new ConnectorSyncMetrics(factory, budget));

        await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);

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
        var (tenant, db) = Tenant();
        using var _ = db;
        const string refusal = "TestConnector did not accept this sign-in.";
        var tokenCache = new ConnectorTokenCache();
        tokenCache.SetSignInFailure("TestConnector", tenant, refusal);
        var configServiceMock = BuildEnabledConfigMock();
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true, Message = "OK" }, NullLogger.Instance);

        await RunAsync(sut, BuildServiceProvider(db, configServiceMock, Enabled(), tokenCache: tokenCache), tenant);

        VerifyHealth(configServiceMock, refusal, healthy: false, "a run that never signed in is not a healthy sync");
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
        var configServiceMock = BuildEnabledConfigMock();
        var root = BuildServiceProvider(db, configServiceMock, Enabled(), tokenCache: tokenCache);
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true, Message = "OK" }, NullLogger.Instance);

        await RunAsync(sut, root, tenantIds[0], slug: "tenant-a");
        await RunAsync(sut, root, tenantIds[1], slug: "tenant-b");

        VerifyHealth(configServiceMock, failure, healthy: false, "the tenant whose sign-in failed");
        VerifyHealth(configServiceMock, string.Empty, healthy: true, "the other tenant, whose sign-in was never in question");
    }

    [Fact]
    public async Task TheSync_ReadsTheRowsTenant_ForRlsIsolation()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        Guid? capturedTenantId = null;
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = true, Message = "OK" },
            NullLogger.Instance,
            onSyncScope: sp => capturedTenantId = sp.GetRequiredService<TenantReadContext>().TenantId);

        await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);

        capturedTenantId.Should().Be(tenant, "the sync runs in the scope the scheduler opened for the row's tenant");
    }

    /// <summary>
    /// The budget is process-wide: a tenant sync in one poller holds a slot that a tenant sync in
    /// another poller has to wait for. The slot gates the tenant's config load, not just the sync
    /// proper — the config load is the connection the unconfigured majority of tenants open.
    /// </summary>
    [Fact]
    public async Task TheBudget_IsSharedAcrossPollers_ASecondPollerWaitsForASlot()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configServiceMock = BuildEnabledConfigMock();
        var budget = new ConnectorSyncBudget(slots: 1);
        using var firstCts = new CancellationTokenSource();

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestConnectorBackgroundService(
            new SyncResult { Success = true },
            NullLogger.Instance,
            onSync: () => firstStarted.TrySetResult(),
            perTenantTimeout: TimeSpan.FromSeconds(30),
            hangFirstNCalls: 1,
            budget: budget);

        var secondConfigLoads = 0;
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TestConnectorBackgroundService(
            new SyncResult { Success = true },
            NullLogger.Instance,
            onSyncCompleted: () => secondDone.TrySetResult(),
            budget: budget);

        var firstRun = RunAsync(first, BuildServiceProvider(db, configServiceMock, Enabled()), tenant, firstCts.Token);
        try
        {
            (await Task.WhenAny(firstStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(firstStarted.Task, "the first poller must take the only slot");

            var secondRun = RunAsync(
                second,
                BuildServiceProvider(db, configServiceMock, Enabled(), onConfigLoad: () => Interlocked.Increment(ref secondConfigLoads)),
                tenant);

            (await Task.WhenAny(secondDone.Task, Task.Delay(TimeSpan.FromMilliseconds(500))))
                .Should().NotBe(secondDone.Task, "the second poller's tenant must wait while the first poller holds the slot");
            secondConfigLoads.Should().Be(0, "the slot must be held before the tenant's config is loaded");
            budget.InFlight.Should().Be(1);

            await firstCts.CancelAsync();

            (await Task.WhenAny(secondDone.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(secondDone.Task, "the slot the first poller released must go to the second");
            await secondRun;
            secondConfigLoads.Should().Be(1);
        }
        finally
        {
            await firstCts.CancelAsync();
            try { await firstRun; } catch (OperationCanceledException) { }
        }

        budget.InFlight.Should().Be(0, "every lease must be returned once the syncs are over");
    }

    /// <summary>
    /// Queueing for a slot is not the tenant's time: its <c>PerTenantSyncTimeout</c> starts once the
    /// slot is held, so a tenant that waited longer than the timeout still gets its full sync.
    /// </summary>
    [Fact]
    public async Task ThePerTenantTimeout_StartsOnlyOnceASlotIsHeld()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var root = BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled());
        var budget = new ConnectorSyncBudget(slots: 1);
        using var firstCts = new CancellationTokenSource();

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestConnectorBackgroundService(
            new SyncResult { Success = true },
            NullLogger.Instance,
            onSync: () => firstStarted.TrySetResult(),
            perTenantTimeout: TimeSpan.FromSeconds(30),
            hangFirstNCalls: 1,
            budget: budget);

        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TestConnectorBackgroundService(
            new SyncResult { Success = true },
            NullLogger.Instance,
            onSyncCompleted: () => secondDone.TrySetResult(),
            perTenantTimeout: TimeSpan.FromMilliseconds(200),
            budget: budget);

        var firstRun = RunAsync(first, root, tenant, firstCts.Token);
        try
        {
            (await Task.WhenAny(firstStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(firstStarted.Task, "the first poller must take the only slot");

            var secondRun = RunAsync(second, root, tenant);

            // Hold the slot for well over the second poller's timeout before releasing it.
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            await firstCts.CancelAsync();

            (await Task.WhenAny(secondDone.Task, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(secondDone.Task, "a tenant that queued longer than its timeout must still run once it holds a slot");
            await secondRun;
        }
        finally
        {
            await firstCts.CancelAsync();
            try { await firstRun; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// A stuck tenant is cancelled at <c>PerTenantSyncTimeout</c> on the injected clock, so it cannot
    /// hold its slots for good, and the run fails so the ledger retries it after its backoff.
    /// </summary>
    [Fact]
    public async Task ASyncExceedingThePerTenantTimeout_IsCancelledAndFailsTheRun()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var logger = new MessageRecordingLogger();
        var budget = new ConnectorSyncBudget();
        var clock = new FakeTimeProvider(Start);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = true },
            logger,
            onSync: () => started.TrySetResult(),
            perTenantTimeout: TimeSpan.FromMinutes(3),
            hangFirstNCalls: 1,
            budget: budget,
            timeProvider: clock);

        var run = RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        run.IsCompleted.Should().BeFalse("the timeout has not passed on the injected clock");

        clock.Advance(TimeSpan.FromSeconds(1));
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(run, "the timeout ends the run");
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<TimeoutException>();
        logger.Messages.Should().Contain(m => m.Contains("exceeded"));
        budget.InFlight.Should().Be(0);
    }

    /// <summary>
    /// A cancellation of neither the run's token nor the timeout's is not the per-tenant timeout, so
    /// it fails the run as itself rather than being reported as one.
    /// </summary>
    [Fact]
    public async Task ASyncThrowingACancellationNobodyAskedFor_FailsTheRunWithoutCallingItATimeout()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var logger = new MessageRecordingLogger();
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = true },
            logger,
            onSync: () => throw new OperationCanceledException());

        var run = () => RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);

        await run.Should().ThrowAsync<OperationCanceledException>();
        logger.Messages.Should().NotContain(m => m.Contains("exceeded"));
    }

    [Fact]
    public async Task ASyncThatThrows_FailsTheRun()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var sut = new TestConnectorBackgroundService(
            new SyncResult { Success = true },
            NullLogger.Instance,
            onSync: () => throw new HttpRequestException("upstream down"));

        var run = () => RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);

        await run.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>A configuration that cannot be read fails the run, which the ledger retries after its backoff.</summary>
    [Fact]
    public async Task AConfigurationThatCannotBeLoaded_FailsTheRunWithoutSyncing()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true }, NullLogger.Instance);

        var run = () => RunAsync(
            sut,
            BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled(),
                configLoadFailure: new InvalidOperationException("secrets unreadable")),
            tenant);

        await run.Should().ThrowAsync<InvalidOperationException>();
        sut.CallCount.Should().Be(0);
    }

    /// <summary>
    /// Almost every tenant has no configuration for almost every connector. A tenant found with no
    /// usable configuration (none, disabled, or no interval) is parked for a day.
    /// </summary>
    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 0)]
    public async Task AnUnconfiguredTenant_IsNotSynced_AndParksForADay(bool enabled, int intervalMinutes)
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configLoads = 0;
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true }, NullLogger.Instance);

        var outcome = await RunAsync(
            sut,
            BuildServiceProvider(db, BuildEnabledConfigMock(),
                new TestConnectorConfig { Enabled = enabled, SyncIntervalMinutes = intervalMinutes },
                onConfigLoad: () => Interlocked.Increment(ref configLoads)),
            tenant);

        sut.CallCount.Should().Be(0);
        configLoads.Should().Be(1);
        DueAt(outcome).Should().Be(Start + PhaseOf(tenant) + TimeSpan.FromDays(1));
    }

    /// <summary>An inactive tenant's row is parked for an hour, so a reactivated tenant needs no reopen.</summary>
    [Fact]
    public async Task AnInactiveTenant_IsNeitherReadNorSynced_AndParksForAnHour()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configLoads = 0;
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true }, NullLogger.Instance);

        var outcome = await RunAsync(
            sut,
            BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled(), onConfigLoad: () => Interlocked.Increment(ref configLoads)),
            tenant,
            isActive: false);

        configLoads.Should().Be(0);
        sut.CallCount.Should().Be(0);
        DueAt(outcome).Should().Be(Start + PhaseOf(tenant) + TimeSpan.FromHours(1));
    }

    /// <summary>
    /// A manual sync holds the same (tenant, connector) key the poller uses, so the run skips the
    /// tenant rather than queue behind it, and the tenant comes due on its interval.
    /// </summary>
    [Fact]
    public async Task WhenASyncIsAlreadyRunning_TheRunSkipsTheTenantWithoutWaiting()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var guard = new TenantRunGuard();
        using var held = guard.TryAcquire(tenant, "testconnector");
        held.Should().NotBeNull();
        var logger = new MessageRecordingLogger();
        var sut = new TestConnectorBackgroundService(new SyncResult { Success = true }, logger, runGuard: guard);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled()), tenant);

        sut.CallCount.Should().Be(0, "a held key makes the run skip the tenant");
        logger.Messages.Should().Contain(m => m.Contains("skipped"));
        DueAt(outcome).Should().Be(Start + TimeSpan.FromMinutes(5) + PhaseOf(tenant));
    }

    private static TestConnectorBackgroundService AlignedPoller(
        Func<DateTime, DateTime?> alignedSync,
        SyncResult? syncResult = null,
        TimeSpan? minAlignedSpacing = null,
        TimeProvider? timeProvider = null) =>
        new(
            syncResult ?? new SyncResult { Success = true },
            NullLogger.Instance,
            alignedSync: alignedSync,
            minAlignedSpacing: minAlignedSpacing,
            timeProvider: timeProvider);

    /// <summary>
    /// A connector that knows when its source next has data brings the next run forward to then: the
    /// interval is the longest the tenant waits, not the shortest. The aligned time is spread by its
    /// own jitter, so it is not moved to the tenant's phase.
    /// </summary>
    [Fact]
    public async Task AnAlignedTimeBeforeTheInterval_IsWhenTheTenantComesDue()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var clock = new FakeTimeProvider(Start);
        var aligned = Start.UtcDateTime.AddMinutes(10).AddSeconds(7);
        var sut = AlignedPoller(_ => aligned, minAlignedSpacing: TimeSpan.Zero, timeProvider: clock);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled(60)), tenant);

        DueAt(outcome).Should().Be(new DateTimeOffset(aligned));
    }

    [Fact]
    public async Task AnAlignedTimeAfterTheInterval_LeavesTheIntervalInCharge()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var clock = new FakeTimeProvider(Start);
        var sut = AlignedPoller(now => now.AddHours(2), minAlignedSpacing: TimeSpan.Zero, timeProvider: clock);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled(60)), tenant);

        DueAt(outcome).Should().Be(Start + TimeSpan.FromHours(1) + PhaseOf(tenant));
    }

    /// <summary>A connector that answers "now" after every sync would otherwise be synced back to back.</summary>
    [Fact]
    public async Task AnAlignedTime_IsNeverSoonerThanTheMinimumSpacing()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var clock = new FakeTimeProvider(Start);
        var sut = AlignedPoller(now => now, minAlignedSpacing: TimeSpan.FromMinutes(5), timeProvider: clock);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled(60)), tenant);

        DueAt(outcome).Should().Be(Start + TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Only a sync that succeeded says anything about the source's cadence; a failing source is left
    /// on the plain interval rather than retried at the aligned time.
    /// </summary>
    [Fact]
    public async Task AFailedSync_IsNotAligned()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var asked = 0;
        var sut = AlignedPoller(
            now => { asked++; return now; },
            syncResult: new SyncResult { Success = false, Message = "down" },
            minAlignedSpacing: TimeSpan.Zero);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, BuildEnabledConfigMock(), Enabled(60)), tenant);

        asked.Should().Be(0);
        DueAt(outcome).Should().Be(Start + TimeSpan.FromHours(1) + PhaseOf(tenant));
    }

    /// <summary>
    /// The alignment is an optimisation over a sync that already succeeded: when working it out
    /// fails, the sync is still reported healthy and the interval still applies.
    /// </summary>
    [Fact]
    public async Task AnAlignmentThatThrows_LeavesTheSyncHealthyAndTheIntervalInCharge()
    {
        var (tenant, db) = Tenant();
        using var _ = db;
        var configServiceMock = BuildEnabledConfigMock();
        var sut = AlignedPoller(_ => throw new InvalidOperationException("no readings table"), minAlignedSpacing: TimeSpan.Zero);

        var outcome = await RunAsync(sut, BuildServiceProvider(db, configServiceMock, Enabled(60)), tenant);

        VerifyHealth(configServiceMock, string.Empty, healthy: true);
        DueAt(outcome).Should().Be(Start + TimeSpan.FromHours(1) + PhaseOf(tenant));
    }

    /// <summary>
    /// The schedule must never hide a configuration the tenant just saved: the configuration
    /// service's cache-invalidation hook reaches the poller through <see cref="ConnectorPollerNudge"/>,
    /// which makes the tenant's row due a minute after each write, so the sync starts no sooner than
    /// a minute after the last write of the flow that made them.
    /// </summary>
    [Fact]
    public void AConfigurationWrite_MakesTheTenantDueAMinuteLater_EveryTime()
    {
        var tenant = Guid.NewGuid();
        var ledger = new Mock<IJobLedger>();
        var nudge = new ConnectorPollerNudge();
        _ = new TestConnectorBackgroundService(new SyncResult(), NullLogger.Instance, nudge: nudge, ledger: ledger.Object);

        // The configuration service names the connector as its row does, lower-case.
        nudge.Invalidate("testconnector", tenant);
        nudge.Invalidate("testconnector", tenant);

        ledger.Verify(l => l.RequestAsync(JobName, tenant, TimeSpan.FromMinutes(1), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// A real-time listener's events make the tenant due now, once per debounce window, so a burst
    /// of upstream changes costs one sync.
    /// </summary>
    [Fact]
    public void AListenerEvent_MakesTheTenantDueNow_OncePerDebounceWindow()
    {
        var tenant = Guid.NewGuid();
        var ledger = new Mock<IJobLedger>();
        var clock = new FakeTimeProvider(Start);
        var sut = new TestConnectorBackgroundService(new SyncResult(), NullLogger.Instance, ledger: ledger.Object, timeProvider: clock);

        sut.RequestImmediateSync(tenant);
        clock.Advance(TimeSpan.FromSeconds(9));
        sut.RequestImmediateSync(tenant);
        ledger.Verify(l => l.RequestAsync(JobName, tenant, It.IsAny<CancellationToken>()), Times.Once);

        clock.Advance(TimeSpan.FromSeconds(1));
        sut.RequestImmediateSync(tenant);
        ledger.Verify(l => l.RequestAsync(JobName, tenant, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void ARequestTheLedgerRefuses_IsLoggedAndLeftToTheTenantsDueTime()
    {
        var ledger = new Mock<IJobLedger>();
        ledger.Setup(l => l.RequestAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var logger = new MessageRecordingLogger();
        var sut = new TestConnectorBackgroundService(new SyncResult(), logger, ledger: ledger.Object);

        sut.Invoking(s => s.RequestImmediateSync(Guid.NewGuid())).Should().NotThrow();
        logger.Messages.Should().Contain(m => m.Contains("Could not request"));
    }

    /// <summary>
    /// A listener that has died must be evicted from the tracking dictionary and disposed so the next
    /// supervision pass can replace it, and the loss of real-time delivery must be visible in the log.
    /// </summary>
    [Fact]
    public async Task ListenerNeedsStart_DeadClient_EvictsDisposesAndLogs()
    {
        var logger = new MessageRecordingLogger();
        var sut = new ListenerHelperService(logger);
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
        var sut = new ListenerHelperService(logger);
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
        var sut = new ListenerHelperService(NullLogger.Instance);

        Assert.True(await sut.ListenerNeedsStartAsync(Guid.NewGuid()));
    }

    /// <summary>Stand-in for a real-time client whose liveness the test controls.</summary>
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

    /// <summary>Exposes the base class's real-time listener helper.</summary>
    private sealed class ListenerHelperService(ILogger logger)
        : ConnectorBackgroundService<TestConnectorConfig>(
            new ConnectorSyncBudget(),
            Mock.Of<IJobLedger>(),
            logger,
            TimeProvider.System)
    {
        public ConcurrentDictionary<Guid, FakeListenerClient> Clients { get; } = new();

        protected override Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            TestConnectorConfig config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null)
            => throw new NotSupportedException();

        public Task<bool> ListenerNeedsStartAsync(Guid tenantId)
            => ListenerNeedsStartAsync(Clients, tenantId, "test-tenant", c => c.IsAlive, c => c.DisposeAsync());
    }

    /// <summary>Records formatted log messages so tests can assert on what was reported.</summary>
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

    private sealed class TestConfigLoader(TestConnectorConfig config, Action? onLoad = null, Exception? failure = null)
        : IConnectorConfigurationLoader<TestConnectorConfig>
    {
        public Task<TestConnectorConfig> LoadForTenantAsync(CancellationToken ct)
        {
            onLoad?.Invoke();
            return failure is null ? Task.FromResult(config) : Task.FromException<TestConnectorConfig>(failure);
        }
    }
}
