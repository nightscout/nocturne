using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.API.Services.Jobs;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Polls one connector for each tenant: the <see cref="IScheduledJob"/> <c>connector:&lt;id&gt;</c>,
/// whose ledger row per tenant holds when that tenant is next due.
/// </summary>
/// <typeparam name="TConfig">
/// The connector configuration type, which must extend <see cref="BaseConnectorConfiguration"/>.
/// </typeparam>
/// <remarks>
/// <para>
/// A run loads the tenant's configuration afresh through
/// <see cref="IConnectorConfigurationLoader{TConfig}"/>, syncs it when it is enabled, and comes due
/// again after <c>SyncIntervalMinutes</c>, or sooner when <see cref="GetAlignedSyncTimeAsync"/> says
/// the source has new data then. A tenant with no usable configuration is parked for
/// <see cref="UnconfiguredRecheckInterval"/>, and a configuration write reopens it a minute later
/// (<see cref="RequestConfigurationRead"/>). A sync that throws or times out fails the run, and the
/// ledger retries it after its backoff, capped at <see cref="ConnectorMaxBackoff"/>.
/// </para>
/// <para>
/// A tenant's row is first due at its <see cref="JobPhase"/> within <see cref="Spread"/>, and every
/// interval due time after that falls on a point of that phase (<see cref="OnPhase"/>), so the tenants
/// of one connector and the connectors of one tenant do not come due together, and a restart (which
/// reads the due times from the ledger) keeps them apart.
/// </para>
/// </remarks>
public abstract class ConnectorBackgroundService<TConfig> : IScheduledJob
    where TConfig : BaseConnectorConfiguration
{
    protected readonly ILogger Logger;

    private static readonly ConnectorRegistrationAttribute Registration =
        ConnectorRegistrationAttribute.DeclaredOn(typeof(TConfig));

    /// <summary>
    /// The sensor cadence the connector declares (see
    /// <see cref="ConnectorRegistrationAttribute.SensorReadingIntervalSeconds"/>), or <c>null</c> when
    /// it declares none or no data source to find its readings by, in which case it is polled on its
    /// interval alone.
    /// </summary>
    private static readonly TimeSpan? SensorCadence =
        Registration.SensorReadingIntervalSeconds > 0 && !string.IsNullOrEmpty(Registration.DataSourceId)
            ? TimeSpan.FromSeconds(Registration.SensorReadingIntervalSeconds)
            : null;

    /// <summary>
    /// The longest a failing sync waits between attempts: shorter than the scheduler's default,
    /// since no data arrives for the tenant while it waits.
    /// </summary>
    public static readonly TimeSpan ConnectorMaxBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The last time a real-time listener's event was let through per tenant; see
    /// <see cref="RequestImmediateSync"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastNudgeByTenant = new();

    private static readonly TimeSpan NudgeDebounceWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum wall-clock time a single tenant's sync may run before it is cancelled. Bounds how long
    /// one stuck or failing tenant — e.g. a hung network call or an auth-retry storm against bad
    /// credentials — can hold a scheduler slot and a budget slot. Overridable for tests.
    /// </summary>
    protected virtual TimeSpan PerTenantSyncTimeout => TimeSpan.FromMinutes(3);

    private readonly ConnectorSyncBudget _budget;
    private readonly IJobLedger _ledger;
    private readonly TimeProvider _timeProvider;
    private readonly ConnectorSyncMetrics? _metrics;

    /// <summary>
    /// Absent, a scheduled sync does not check for one already in flight; production supplies the
    /// singleton so a manual sync of the same tenant and connector makes the scheduled one skip.
    /// </summary>
    private readonly TenantRunGuard? _runGuard;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Initialises a new <see cref="ConnectorBackgroundService{TConfig}"/>.
    /// </summary>
    /// <param name="budget">The process-wide budget.</param>
    /// <param name="ledger">The job ledger a configuration write or listener event makes a tenant due in.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="timeProvider">Clock the schedule is computed against.</param>
    /// <param name="nudge">Delivers configuration writes for this connector; absent, a change is noticed on the tenant's next run.</param>
    /// <param name="metrics">Connector sync instruments; absent, the sync runs unmeasured.</param>
    /// <param name="runGuard">Refuses a sync for a key another run already holds; absent, no exclusion.</param>
    protected ConnectorBackgroundService(
        ConnectorSyncBudget budget,
        IJobLedger ledger,
        ILogger logger,
        TimeProvider timeProvider,
        ConnectorPollerNudge? nudge = null,
        ConnectorSyncMetrics? metrics = null,
        TenantRunGuard? runGuard = null
    )
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _metrics = metrics;
        _runGuard = runGuard;
        nudge?.Subscribe(ConnectorName, RequestConfigurationRead);
    }

    /// <summary>
    /// <c>connector:&lt;id&gt;</c>, the audit endpoint a manual sync of the connector is attributed under.
    /// </summary>
    public string Name => AuditEndpoint;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public TimeSpan MaxBackoff => ConnectorMaxBackoff;

    /// <summary>A minute: the shortest interval a tenant can configure.</summary>
    public TimeSpan Spread => TimeSpan.FromMinutes(1);

    /// <summary>
    /// Leaves slots to the other jobs while every connector's tenants are stuck against unreachable
    /// upstreams, each run held to <see cref="PerTenantSyncTimeout"/>: the cap is shared by every
    /// connector (<see cref="ConcurrencyGroup"/>), so the short jobs, the alert sweep among them, keep
    /// the slots it leaves.
    /// </summary>
    public int MaxConcurrency => 8;

    public string ConcurrencyGroup => ConnectorsGroup;

    internal const string ConnectorsGroup = "connectors";

    /// <summary>
    /// How long after a configuration write the tenant is synced: a connect flow writes its
    /// configuration several times (OAuth state, tokens, inventory, options), and the sync starts no
    /// sooner than this after the last write.
    /// </summary>
    internal static readonly TimeSpan ConfigurationSettleDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Makes the tenant due <see cref="ConfigurationSettleDelay"/> after a configuration write, which
    /// <see cref="ConnectorPollerNudge"/> delivers, whenever it was due before. A request made while
    /// the tenant's sync runs is kept, and the run that follows reads it.
    /// </summary>
    private void RequestConfigurationRead(Guid tenantId) => _ = RequestAsync(tenantId, ConfigurationSettleDelay);

    /// <summary>
    /// Makes the tenant due now for a real-time listener's event. Events within
    /// <see cref="NudgeDebounceWindow"/> of the last one let through for the same tenant are dropped,
    /// so a burst of upstream changes costs one sync rather than one each.
    /// </summary>
    /// <param name="tenantId">The tenant to sync.</param>
    protected void RequestImmediateSync(Guid tenantId)
    {
        var now = UtcNow;
        if (_lastNudgeByTenant.TryGetValue(tenantId, out var lastNudge) && now - lastNudge < NudgeDebounceWindow)
            return;

        _lastNudgeByTenant[tenantId] = now;
        _ = RequestAsync(tenantId, TimeSpan.Zero);
    }

    private async Task RequestAsync(Guid tenantId, TimeSpan after)
    {
        try
        {
            await (after == TimeSpan.Zero
                ? _ledger.RequestAsync(Name, tenantId, CancellationToken.None)
                : _ledger.RequestAsync(Name, tenantId, after, CancellationToken.None));
            Logger.LogDebug(
                "Sync requested for {ConnectorName} tenant {TenantId} in {After}",
                ConnectorName, tenantId, after);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Could not request a {ConnectorName} sync for tenant {TenantId}; it syncs at its next due time",
                ConnectorName, tenantId);
        }
    }

    /// <summary>
    /// The connector's configuration-section name; must match the name its stored health state is
    /// filed under.
    /// </summary>
    /// <seealso cref="ConnectorRegistrationAttribute.DeclaredOn"/>
    protected static string ConnectorName => Registration.ConnectorName;

    /// <summary>
    /// The audit endpoint a real-time listener's scope is attributed under, the one
    /// <c>ConnectorSyncService</c> uses for a manual sync too.
    /// </summary>
    protected static string AuditEndpoint => $"connector:{Registration.ConnectorId}";

    /// <summary>
    /// How long a tenant with no usable configuration is parked: enablement can change without a
    /// configuration write (platform options from the environment, a secret that decrypts again after
    /// a key restore), and a write's request still reopens the row within a minute.
    /// </summary>
    internal static readonly TimeSpan UnconfiguredRecheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The earliest an aligned sync may be scheduled after the sync that produced it. Guards the
    /// source against a <see cref="GetAlignedSyncTimeAsync"/> that answers "now" on every run, which
    /// would otherwise sync the tenant back to back. Overridable for tests.
    /// </summary>
    protected virtual TimeSpan MinimumAlignedSyncSpacing => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Called after each successful sync to say when the tenant should next be synced, ahead of
    /// <c>SyncIntervalMinutes</c>, or <c>null</c> to leave the interval in charge. For a connector
    /// that declares a sensor cadence this is just after its next reading should reach the cloud (see
    /// <see cref="SensorSyncAlignment"/>, whose own jitter spreads it), worked out from the newest
    /// reading stored under the connector's data source; every other connector answers <c>null</c>.
    /// Overridable for tests.
    /// </summary>
    /// <param name="scopeProvider">The tenant-scoped provider the sync ran in.</param>
    /// <param name="config">The tenant's connector configuration.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected virtual async Task<DateTime?> GetAlignedSyncTimeAsync(
        IServiceProvider scopeProvider,
        TConfig config,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (SensorCadence is not { } cadence)
            return null;

        var publisher = scopeProvider.GetService<IGlucosePublisher>();
        if (publisher is null)
            return null;

        var latest = await publisher.GetLatestSensorGlucoseTimestampAsync(
            Registration.DataSourceId, cancellationToken);
        if (latest is not { } reading)
            return null;

        reading = reading.Kind switch
        {
            DateTimeKind.Local => reading.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(reading, DateTimeKind.Utc),
            _ => reading,
        };

        return SensorSyncAlignment.NextSyncAt(reading, cadence, now, Random.Shared.NextDouble());
    }

    /// <summary>
    /// Reports whether a real-time listener must be started for a tenant, evicting and disposing a
    /// tracked client that <paramref name="isAlive"/> rejects so the caller can replace it. The loss of
    /// real-time delivery is logged at the point of eviction.
    /// </summary>
    protected async Task<bool> ListenerNeedsStartAsync<TClient>(
        ConcurrentDictionary<Guid, TClient> clients,
        Guid tenantId,
        string tenantSlug,
        Func<TClient, bool> isAlive,
        Func<TClient, Task> disposeAsync)
    {
        if (!clients.TryGetValue(tenantId, out var existing))
            return true;

        if (isAlive(existing))
            return false;

        clients.TryRemove(tenantId, out _);

        Logger.LogWarning(
            "{ConnectorName} real-time listener for tenant {TenantSlug} is no longer connected; polling only until it is re-established",
            ConnectorName, tenantSlug);

        try
        {
            await disposeAsync(existing);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Error disposing dead {ConnectorName} real-time listener for tenant {TenantSlug}",
                ConnectorName, tenantSlug);
        }

        return true;
    }

    /// <summary>
    /// The tenant's configured instance URL as an absolute origin, or null when the stored value
    /// cannot be read as one. A listener cannot reach an unresolvable URL and the tenant's polling
    /// path rejects it in the same words, so this reports it against the listener and leaves the
    /// caller to fall back to polling rather than raising it as an unexpected failure.
    /// </summary>
    protected string? ResolveListenerBaseUrl(string? url, string tenantSlug)
    {
        try
        {
            return ConnectorUrl.ResolveBase(url, ConnectorName);
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(
                "{ConnectorName} URL for tenant {TenantSlug} cannot be resolved to an absolute http(s) URL ({Reason}), will rely on polling",
                ConnectorName, tenantSlug, ex.Message);

            return null;
        }
    }

    /// <summary>
    /// Performs a single sync operation using the connector service.
    /// Services should be resolved from the provided <paramref name="scopeProvider"/>
    /// which has the tenant context already set.
    /// </summary>
    /// <param name="scopeProvider">Tenant-scoped service provider</param>
    /// <param name="config">Per-tenant connector configuration loaded by the framework</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="progressReporter">Optional progress reporter for sync status updates</param>
    /// <returns>A SyncResult indicating success/failure and any error details</returns>
    protected abstract Task<SyncResult> PerformSyncAsync(
        IServiceProvider scopeProvider,
        TConfig config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null);

    /// <summary>
    /// Persists the health state for this connector to the database via <see cref="IConnectorConfigurationService"/>.
    /// Errors are swallowed and logged as warnings so that health-state failures do not abort sync.
    /// </summary>
    private async Task UpdateHealthStateAsync(
        IServiceProvider scopeProvider,
        DateTime? lastSyncAttempt = null,
        DateTime? lastSuccessfulSync = null,
        string? lastErrorMessage = null,
        DateTime? lastErrorAt = null,
        bool? isHealthy = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var configService = scopeProvider.GetRequiredService<IConnectorConfigurationService>();

            await configService.UpdateHealthStateAsync(
                ConnectorName,
                lastSyncAttempt,
                lastSuccessfulSync,
                lastErrorMessage,
                lastErrorAt,
                isHealthy,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to update health state for {ConnectorName}",
                ConnectorName
            );
        }
    }

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenant = run.Tenant!;
        if (!tenant.IsActive)
            return JobOutcome.Resume(null, run.StartedAt + IScheduledJob.InactiveTenantRecheck);

        // Waiting for a slot is not the tenant's time, so the timeout starts once one is held.
        using var slot = await AcquireSlotAsync(tenant.Slug, ct);

        using var timeout = new CancellationTokenSource(PerTenantSyncTimeout, _timeProvider);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            return await SyncAsync(run, ct, stopping.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Logger.LogWarning(
                "{ConnectorName} sync for tenant {TenantSlug} exceeded {Timeout} and was cancelled",
                ConnectorName, tenant.Slug, PerTenantSyncTimeout);
            throw new TimeoutException($"The {ConnectorName} sync exceeded {PerTenantSyncTimeout}.");
        }
    }

    /// <summary>
    /// How long a tenant may queue for a budget slot before the wait is reported. A saturated budget
    /// otherwise shows only as syncs running late.
    /// </summary>
    protected virtual TimeSpan SlotWaitWarningAfter => TimeSpan.FromSeconds(30);

    private async Task<ConnectorSyncBudget.Lease> AcquireSlotAsync(string tenantSlug, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();

        var acquire = _budget.AcquireAsync(stoppingToken);
        if (acquire.IsCompleted)
        {
            var fastPath = await acquire;
            _metrics?.RecordSlotWait(ConnectorName, Stopwatch.GetElapsedTime(started));
            return fastPath;
        }

        var pending = acquire.AsTask();
        while (await Task.WhenAny(pending, Task.Delay(SlotWaitWarningAfter, stoppingToken)) != pending)
        {
            Logger.LogWarning(
                "{ConnectorName} sync for tenant {TenantSlug} has waited {Waited} for a sync slot; {InFlight} of {Slots} in use",
                ConnectorName, tenantSlug, Stopwatch.GetElapsedTime(started), _budget.InFlight, _budget.Slots);
        }

        var held = await pending;
        _metrics?.RecordSlotWait(ConnectorName, Stopwatch.GetElapsedTime(started));
        return held;
    }

    private async Task<JobOutcome> SyncAsync(JobRun run, CancellationToken shutdownToken, CancellationToken stoppingToken)
    {
        var tenantId = run.Tenant!.TenantId;
        var tenantSlug = run.Tenant.Slug;
        var config = await run.Services.GetRequiredService<IConnectorConfigurationLoader<TConfig>>()
            .LoadForTenantAsync(stoppingToken);

        if (!config.Enabled || config.SyncIntervalMinutes <= 0)
            return JobOutcome.Resume(null, run.StartedAt + UnconfiguredRecheckInterval);

        var intervalDue = OnPhase(run.StartedAt, TimeSpan.FromMinutes(config.SyncIntervalMinutes), tenantId);

        // A manual sync of the same tenant and connector holds this key for its whole run. This
        // poller never waits: the manual run is already fetching this tenant's data.
        using var lease = _runGuard?.TryAcquire(tenantId, Registration.ConnectorId);
        if (_runGuard is not null && lease is null)
        {
            Logger.LogInformation(
                "{ConnectorName} sync for tenant {TenantSlug} skipped: a sync is already running",
                ConnectorName, tenantSlug);
            return JobOutcome.Resume(null, intervalDue);
        }

        Logger.LogDebug("Syncing {ConnectorName} for tenant {TenantSlug}", ConnectorName, tenantSlug);

        await UpdateHealthStateAsync(
            run.Services,
            lastSyncAttempt: UtcNow,
            cancellationToken: stoppingToken);

        var started = Stopwatch.GetTimestamp();
        var progressReporter = run.Services.GetService<ISyncProgressReporter>();
        SyncResult result;
        try
        {
            result = await PerformSyncAsync(run.Services, config, stoppingToken, progressReporter);
        }
        catch (OperationCanceledException)
        {
            _metrics?.RecordSyncDuration(
                ConnectorName,
                shutdownToken.IsCancellationRequested ? "cancelled"
                    : stoppingToken.IsCancellationRequested ? "timeout"
                    : "failure",
                Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch (Exception)
        {
            _metrics?.RecordSyncDuration(ConnectorName, "failure", Stopwatch.GetElapsedTime(started));
            throw;
        }

        // A run that never got a token has nothing to fetch, which several connectors report as a
        // successful sync that found no data. Reading the failure here rather than in each connector
        // is what makes a connector that cannot sign in visible for all of them.
        var signInFailure = run.Services.GetRequiredService<IConnectorTokenCache>()
            .GetSignInFailure(ConnectorName, tenantId);

        _metrics?.RecordSyncDuration(
            ConnectorName,
            result.Success && signInFailure == null ? "success" : "failure",
            Stopwatch.GetElapsedTime(started));

        if (result.Success && signInFailure == null)
        {
            Logger.LogInformation(
                "{ConnectorName} sync completed for tenant {TenantSlug}",
                ConnectorName, tenantSlug);

            await UpdateHealthStateAsync(
                run.Services,
                lastSuccessfulSync: UtcNow,
                isHealthy: true,
                lastErrorMessage: string.Empty,
                lastErrorAt: DateTime.MinValue,
                cancellationToken: stoppingToken);

            var aligned = await AlignedSyncAtAsync(run.Services, tenantSlug, config, stoppingToken);
            return JobOutcome.Resume(null, aligned < intervalDue ? aligned.Value : intervalDue);
        }

        // Distinct because the same message repeats per chunk; see
        // ConnectorConfigurationRecord.LastErrorMessageMaxLength. A failed sign-in outranks
        // whatever the run made of it, because it names the step that stopped the run.
        var errorMessage = signInFailure
            ?? (result.Errors.Count > 0
                ? string.Join("; ", result.Errors.Distinct(StringComparer.Ordinal))
                : !string.IsNullOrWhiteSpace(result.Message)
                    ? result.Message
                    : "Sync failed");

        Logger.LogWarning(
            "{ConnectorName} sync failed for tenant {TenantSlug}: {ErrorMessage}",
            ConnectorName, tenantSlug, errorMessage);

        await UpdateHealthStateAsync(
            run.Services,
            isHealthy: false,
            lastErrorMessage: errorMessage,
            lastErrorAt: UtcNow,
            cancellationToken: stoppingToken);

        return JobOutcome.Resume(null, intervalDue);
    }

    /// <summary>
    /// When the connector says its source next has new data, no sooner than
    /// <see cref="MinimumAlignedSyncSpacing"/> from now, or null. A failure here is logged and costs
    /// only the alignment: the sync already succeeded, and the plain interval still stands.
    /// </summary>
    private async Task<DateTimeOffset?> AlignedSyncAtAsync(
        IServiceProvider scopeProvider,
        string tenantSlug,
        TConfig config,
        CancellationToken cancellationToken)
    {
        DateTime? aligned;
        try
        {
            aligned = await GetAlignedSyncTimeAsync(scopeProvider, config, UtcNow, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "Could not align the next {ConnectorName} sync for tenant {TenantSlug}; using the sync interval",
                ConnectorName, tenantSlug);
            return null;
        }

        if (aligned is not { } at)
            return null;

        var earliest = UtcNow + MinimumAlignedSyncSpacing;
        if (at < earliest)
            at = earliest;

        Logger.LogDebug(
            "{ConnectorName} next sync for tenant {TenantSlug} aligned to {AlignedAt:HH:mm:ss} UTC",
            ConnectorName, tenantSlug, at);

        return new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
    }

    /// <summary>
    /// The first point of the tenant's <see cref="JobPhase"/> at or after <paramref name="interval"/>
    /// from <paramref name="startedAt"/>, less two <see cref="JobScheduler.PollInterval"/>s: a run the
    /// scheduler started a poll or two late keeps its phase rather than skipping a whole step, and no
    /// run comes due sooner than that after the last.
    /// </summary>
    private DateTimeOffset OnPhase(DateTimeOffset startedAt, TimeSpan interval, Guid tenantId)
    {
        var phase = JobPhase.Of(Name, tenantId, Spread);
        var period = Spread.Ticks;
        var earliest = (startedAt + interval - 2 * JobScheduler.PollInterval - phase).UtcTicks;
        var point = (earliest + period - 1) / period * period;
        return new DateTimeOffset(point, TimeSpan.Zero) + phase;
    }
}

/// <summary>
/// Polls <typeparamref name="TService"/> on the schedule <typeparamref name="TConfig"/> configures.
/// <c>AddConnectors</c> closes this over every connector that registers a sync executor and has no
/// subclass of its own, so a connector needs no scheduling code to be polled.
/// </summary>
public class ConnectorBackgroundService<TService, TConfig>(
    ConnectorSyncBudget budget,
    IJobLedger ledger,
    ILogger<ConnectorBackgroundService<TService, TConfig>> logger,
    TimeProvider timeProvider,
    ConnectorPollerNudge? nudge = null,
    ConnectorSyncMetrics? metrics = null,
    TenantRunGuard? runGuard = null)
    : ConnectorBackgroundService<TConfig>(budget, ledger, logger, timeProvider, nudge, metrics, runGuard)
    where TService : class, IConnectorService<TConfig>
    where TConfig : BaseConnectorConfiguration
{
    protected sealed override Task<SyncResult> PerformSyncAsync(
        IServiceProvider scopeProvider,
        TConfig config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null) =>
        scopeProvider.GetRequiredService<TService>()
            .SyncDataAsync(config, cancellationToken, since: null, progressReporter);
}
