using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Base implementation for connector services with common Nightscout upload functionality
/// </summary>
/// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
public abstract class BaseConnectorService<TConfig> : IConnectorService<TConfig>
    where TConfig : BaseConnectorConfiguration
{
    protected readonly HttpClient _httpClient;
    protected readonly IConnectorServerResolver<TConfig> _serverResolver;
    protected readonly ILogger _logger;
    private readonly IConnectorPublisher? _publisher;

    /// <summary>The API publisher, or <c>null</c> when running detached (e.g. dry-run tooling).</summary>
    protected IConnectorPublisher? Publisher => _publisher;

    // Broadcast origin for this run's glucose / care (treatment-family) publishes, resolved once from the
    // pre-run resume watermark and memoized so every batch and granular publish in the run agrees — a
    // paginated or multi-call first sync can't flip to Live mid-backfill. The connector service is
    // resolved fresh per sync run, so these are naturally per-run.
    private WriteOrigin? _glucosePublishOrigin;
    private WriteOrigin? _treatmentPublishOrigin;
    private WriteOrigin? _devicePublishOrigin;

    // Carried on the instance rather than through PerformSyncInternalAsync's callees so the shared
    // publish path can report without every connector threading it; safe for the same reason the
    // publish-origin memos above are.
    private ISyncProgressReporter? _progressReporter;

    /// <summary>
    ///     Base constructor for connector services using IHttpClientFactory pattern
    /// </summary>
    /// <param name="httpClient">HttpClient instance from IHttpClientFactory (will not be disposed)</param>
    /// <param name="serverResolver">Resolves the base server URL from per-tenant config</param>
    /// <param name="logger">Logger instance for this connector</param>
    /// <param name="publisher">Optional publisher for Nocturne mode</param>
    protected BaseConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TConfig> serverResolver,
        ILogger logger,
        IConnectorPublisher? publisher = null
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serverResolver = serverResolver ?? throw new ArgumentNullException(nameof(serverResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publisher = publisher;
    }

    /// <summary>
    ///     Unique identifier for this connector service type
    /// </summary>
    protected abstract string ConnectorSource { get; }

    public abstract string ServiceName { get; }

    /// <summary>
    /// The data types this connector fetches, read from the <see cref="ConnectorRegistrationAttribute"/>
    /// on <typeparamref name="TConfig"/>. The attribute drives the tenant-facing toggle schema and
    /// this property drives the sync loop, so stating them separately lets a connector advertise a
    /// toggle it never acts on, or act on data the tenant has no way to turn off.
    /// </summary>
    public virtual List<SyncDataType> SupportedDataTypes => [.. RegisteredDataTypes];

    private static readonly SyncDataType[] RegisteredDataTypes =
        typeof(TConfig).GetCustomAttribute<ConnectorRegistrationAttribute>()?.SupportedDataTypes
        ?? [SyncDataType.Glucose];

    /// <summary>
    ///     The pre-flight <see cref="RunBackgroundSyncAsync"/> runs before it fetches anything.
    ///     It carries no configuration, so a connector whose credentials are per-tenant has nothing
    ///     to authenticate against here and admits the run: it resolves and checks the tenant's
    ///     credential inside <see cref="PerformSyncInternalAsync"/>, where the config is in hand.
    ///     Only a connector holding a process-wide credential overrides this.
    /// </summary>
    public virtual Task<bool> AuthenticateAsync()
    {
        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public virtual async Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        return await RunWithProgressAsync(
            progressReporter,
            cancellationToken,
            async () => await EnsureAuthenticatedAsync(config, cancellationToken)
                ? await PerformSyncInternalAsync(request, config, cancellationToken)
                : AuthenticationFailedResult());
    }

    /// <summary>
    ///     Hand-shake run before <see cref="PerformSyncInternalAsync"/> on the requested-range entry
    ///     point. Connectors that must authenticate before they can fetch override this instead of the
    ///     <see cref="SyncRequest"/> overload, so a rejected credential still passes through
    ///     <see cref="RunWithProgressAsync"/> and produces the run's one terminal progress message.
    ///     The background entry point authenticates through <see cref="AuthenticateAsync"/> in
    ///     <see cref="RunBackgroundSyncAsync"/> and never reaches this overload, so a connector
    ///     overriding both is not authenticated twice for one run.
    /// </summary>
    protected virtual Task<bool> EnsureAuthenticatedAsync(
        TConfig config,
        CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    ///     The result of a run that never got past authentication. Carries the detail in
    ///     <see cref="SyncResult.Errors"/> and the summary in <see cref="SyncResult.Message"/>
    ///     because the terminal progress message reads the former and the tenant's sync card the latter.
    /// </summary>
    protected SyncResult AuthenticationFailedResult()
    {
        var now = DateTimeOffset.UtcNow;
        return new SyncResult
        {
            Success = false,
            StartTime = now,
            EndTime = now,
            Message = "Authentication failed",
            Errors = { $"Authentication failed for {ConnectorSource}" },
        };
    }

    /// <summary>
    ///     Runs one sync for the lifetime of <paramref name="progressReporter"/> and emits the
    ///     run's terminal progress message. Owned here rather than by each connector so every
    ///     sync reaches a terminal <see cref="SyncPhase"/> and the tenant's in-progress indicator
    ///     always resolves — including when the run never got as far as fetching data.
    /// </summary>
    private async Task<SyncResult> RunWithProgressAsync(
        ISyncProgressReporter? progressReporter,
        CancellationToken cancellationToken,
        Func<Task<SyncResult>> body
    )
    {
        _progressReporter = progressReporter;
        try
        {
            var result = await body();
            StandInFailureMessage(result);
            await ReportSyncOutcomeAsync(result.Success, FailureMessage(result), cancellationToken);
            return result;
        }
        // A cancelled run has no outcome to report — the caller withdrew it. The background
        // entry point's own catch-all converts its timeout into a failed result first, so that
        // path still reports a terminal message through the success path above.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReportSyncOutcomeAsync(false, ex.Message, cancellationToken);
            throw;
        }
        finally
        {
            _progressReporter = null;
        }
    }

    private Task ReportSyncOutcomeAsync(bool success, string? errorMessage, CancellationToken cancellationToken) =>
        ReportSyncMessageAsync(
            success ? SyncMessageType.SyncComplete : SyncMessageType.SyncFailed,
            null, cancellationToken, errorMessage);

    /// <summary>
    ///     Gives a failed run that recorded only <see cref="SyncResult.Errors"/> a
    ///     <see cref="SyncResult.Message"/>, standing in the failure that started it.
    /// </summary>
    /// <remarks>
    ///     The manual-sync dialog shows <see cref="SyncResult.Message"/> and nothing else about a
    ///     failure — not <see cref="SyncResult.Errors"/> — so a run that recorded its reason only in
    ///     the errors puts that reason out of the tenant's reach entirely. Owned here because every
    ///     connector's failure paths converge on this wrapper, unlike the per-type catch blocks that
    ///     raise most of them: those sit in each connector separately and can hold no shared rule.
    ///     A message an inner path chose stands, because <see cref="AuthenticationFailedResult"/>
    ///     and <see cref="RecordFailure"/> both summarise what the raw error text only implies; as
    ///     in the latter, the first recorded failure names the run.
    /// </remarks>
    private static void StandInFailureMessage(SyncResult result)
    {
        if (result.Success || result.Errors.Count == 0) return;

        if (string.IsNullOrWhiteSpace(result.Message))
            result.Message = result.Errors[0];
    }

    private static string? FailureMessage(SyncResult result)
    {
        if (result.Success) return null;
        return result.Errors.Count > 0
            ? string.Join("; ", result.Errors)
            : string.IsNullOrWhiteSpace(result.Message) ? null : result.Message;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Get the timestamp of the most recent entry from the Nocturne API
    ///     This enables "catch up" functionality to fetch only new data since the last upload
    /// </summary>
    private async Task<DateTime?> FetchLatestEntryTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest entry timestamp"
            );
            return null;
        }

        try
        {
            var timestamp = await _publisher.Glucose.GetLatestEntryTimestampAsync(ConnectorSource);
            if (timestamp.HasValue)
                _logger.LogInformation(
                    "Latest entry timestamp from API for {ConnectorSource}: {Timestamp:yyyy-MM-dd HH:mm:ss} UTC",
                    ConnectorSource,
                    timestamp.Value
                );
            else
                _logger.LogDebug(
                    "No existing entries found for {ConnectorSource}",
                    ConnectorSource
                );
            return timestamp;
        }
        catch (Exception ex)
        {
            // Do not swallow: a null watermark means "no prior data", which triggers an
            // initial backfill — unbounded for connectors with a null InitialSyncFloor.
            // A transient read failure must fail this cycle (retried next interval), not
            // be amplified into a full-history recrawl and republish.
            _logger.LogError(
                ex,
                "Failed to fetch latest entry timestamp for {ConnectorSource}",
                ConnectorSource
            );
            throw;
        }
    }

    /// <summary>
    ///     Get the timestamp of the most recent treatment from the Nocturne API
    ///     This enables "catch up" functionality to fetch only new data since the last upload
    /// </summary>
    private async Task<DateTime?> FetchLatestTreatmentTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest treatment timestamp"
            );
            return null;
        }

        try
        {
            var timestamp = await _publisher.Treatments.GetLatestTreatmentTimestampAsync(ConnectorSource);
            if (timestamp.HasValue)
                _logger.LogInformation(
                    "Latest treatment timestamp from API for {ConnectorSource}: {Timestamp:yyyy-MM-dd HH:mm:ss} UTC",
                    ConnectorSource,
                    timestamp.Value
                );
            else
                _logger.LogDebug(
                    "No existing treatments found for {ConnectorSource}",
                    ConnectorSource
                );
            return timestamp;
        }
        catch (Exception ex)
        {
            // See FetchLatestEntryTimestampAsync: a swallowed failure reads as "no prior
            // data" and triggers an unbounded initial backfill for null-floor connectors.
            _logger.LogError(
                ex,
                "Failed to fetch latest treatment timestamp for {ConnectorSource}",
                ConnectorSource
            );
            throw;
        }
    }

    /// <summary>
    ///     Get the timestamp of the most recent device status from the Nocturne API
    ///     This enables independent "catch up" for device status, decoupled from glucose
    /// </summary>
    private async Task<DateTime?> FetchLatestDeviceStatusTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest device status timestamp"
            );
            return null;
        }

        try
        {
            return await _publisher.Device.GetLatestDeviceStatusTimestampAsync(ConnectorSource);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch latest device status timestamp for {ConnectorSource}",
                ConnectorSource
            );
            return null;
        }
    }

    /// <summary>
    ///     Get the timestamp of the most recent activity record from the Nocturne API
    ///     This enables independent "catch up" for activity, decoupled from glucose
    /// </summary>
    private async Task<DateTime?> FetchLatestActivityTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest activity timestamp"
            );
            return null;
        }

        try
        {
            return await _publisher.Metadata.GetLatestActivityTimestampAsync(ConnectorSource);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch latest activity timestamp for {ConnectorSource}",
                ConnectorSource
            );
            return null;
        }
    }

    /// <summary>
    ///     Calculate the optimal "since" timestamp for fetching glucose entries
    ///     Uses catch-up logic to fetch from the most recent entry, or falls back to default lookback
    /// </summary>
    protected async Task<DateTime?> CalculateSinceTimestampAsync(
        TConfig config,
        DateTime? defaultSince = null
    )
    {
        if (defaultSince.HasValue)
            return defaultSince.Value;

        // Get the most recent entry timestamp from Nocturne API
        var latestEntryTimestamp = await FetchLatestEntryTimestampAsync(config);

        return CalculateSinceFromTimestamp(latestEntryTimestamp, "entries");
    }

    /// <summary>
    ///     Calculate the optimal "since" timestamp for fetching treatments
    ///     Uses catch-up logic to fetch from the most recent treatment, or falls back to default lookback
    /// </summary>
    protected async Task<DateTime?> CalculateTreatmentSinceTimestampAsync(
        TConfig config,
        DateTime? defaultSince = null
    )
    {
        if (defaultSince.HasValue)
            return defaultSince.Value;

        // Get the most recent treatment timestamp from Nocturne API
        var latestTreatmentTimestamp = await FetchLatestTreatmentTimestampAsync(config);

        return CalculateSinceFromTimestamp(latestTreatmentTimestamp, "treatments");
    }

    /// <summary>
    ///     Calculate an independent catch-up "since" timestamp for device status.
    ///     Returns the most recent device-status timestamp (minus a small overlap), or
    ///     <c>null</c> when none exists — letting the caller decide its own fallback
    ///     rather than forcing a full initial-window re-fetch of high-volume telemetry.
    /// </summary>
    protected async Task<DateTime?> CalculateDeviceStatusCatchUpSinceAsync(TConfig config)
    {
        var latest = await FetchLatestDeviceStatusTimestampAsync(config);
        return TryCalculateCatchUpSince(latest, "device status");
    }

    /// <summary>
    ///     Calculate an independent catch-up "since" timestamp for activity.
    ///     Returns the most recent activity timestamp (minus a small overlap), or
    ///     <c>null</c> when none exists so the caller can choose its own fallback.
    /// </summary>
    protected async Task<DateTime?> CalculateActivityCatchUpSinceAsync(TConfig config)
    {
        var latest = await FetchLatestActivityTimestampAsync(config);
        return TryCalculateCatchUpSince(latest, "activity");
    }

    /// <summary>
    ///     The lower bound a family crawls from, given the caller's bound and the family's own
    ///     resume point: whichever of the two reaches further back, where an open resume point
    ///     reaches back without limit and an absent caller bound leaves the resume point standing.
    /// </summary>
    /// <remarks>
    ///     Neither bound may narrow the other. The resume point cannot narrow the caller's, because
    ///     an explicit <c>from</c> with no <c>to</c> is the shape an admin repairing a gap sends, and
    ///     answering that from the watermark returns nothing and reports it as a success. The
    ///     caller's cannot narrow the resume point either, because on a background catch-up the
    ///     caller's bound is the glucose watermark, and honouring it alone is what strands the other
    ///     families behind glucose — a resume point left open by an unbounded
    ///     <see cref="InitialSyncFloor"/> included, since that is a family with nothing stored at
    ///     all asking for the source's whole history.
    ///     <para>
    ///     A caller that supplies no bound is not asking for everything: a background cycle whose
    ///     glucose watermark is null reaches here, as does the tenant's own sync button, and a
    ///     family with a resume point still stands on it. A resume point that is absent rather than
    ///     open is resolved by the caller before it gets here — see
    ///     <see cref="CalculateDeviceStatusCatchUpSinceAsync"/> for the two that answer that way.
    ///     </para>
    /// </remarks>
    protected static DateTime? ResumeFrom(DateTime? requested, DateTime? resumePoint)
    {
        if (requested is null)
            return resumePoint;

        if (resumePoint is null)
            return null;

        return requested < resumePoint ? requested : resumePoint;
    }

    /// <summary>
    ///     Applies the catch-up overlap to a latest-record timestamp: returns the timestamp
    ///     minus a small overlap (to absorb clock drift), or <c>null</c> when there is no
    ///     usable prior timestamp.
    /// </summary>
    private DateTime? TryCalculateCatchUpSince(DateTime? latestTimestamp, string dataType)
    {
        if (latestTimestamp.HasValue && latestTimestamp.Value > DateTime.MinValue.AddMinutes(10))
        {
            // Add a small overlap to ensure we don't miss any data due to clock drift
            var sinceWithOverlap = latestTimestamp.Value.AddMinutes(-5);

            _logger?.LogInformation(
                "Starting catch-up sync for {DataType} from {ConnectorSource} since {Since:yyyy-MM-dd HH:mm:ss} UTC",
                dataType,
                ConnectorSource,
                sinceWithOverlap
            );
            return sinceWithOverlap;
        }

        return null;
    }

    /// <summary>
    ///     Helper method to calculate the since timestamp from a latest timestamp.
    ///     When no prior data exists, falls back to <see cref="InitialSyncFloor"/> — which may be
    ///     <c>null</c> (no lower bound) for connectors that import the source's full history.
    /// </summary>
    private DateTime? CalculateSinceFromTimestamp(DateTime? latestTimestamp, string dataType)
    {
        var catchUpSince = TryCalculateCatchUpSince(latestTimestamp, dataType);
        if (catchUpSince.HasValue)
            return catchUpSince.Value;

        // No prior data: this is the initial sync. Most connectors bound the first backfill to
        // InitialSyncFloor; a null floor means "no lower bound" — import the source's full history.
        var fallbackSince = InitialSyncFloor;
        if (fallbackSince.HasValue)
            _logger?.LogInformation(
                "No existing {DataType} found for {ConnectorSource}, performing initial sync from {Since:yyyy-MM-dd HH:mm:ss} UTC",
                dataType,
                ConnectorSource,
                fallbackSince.Value
            );
        else
            _logger?.LogInformation(
                "No existing {DataType} found for {ConnectorSource}, performing initial sync over the source's full history",
                dataType,
                ConnectorSource
            );
        return fallbackSince;
    }

    /// <summary>
    ///     Lower bound applied to an initial sync when no prior data exists for a data type.
    ///     Connectors whose source is a full data export (e.g. Nightscout) override this to return
    ///     <c>null</c> so the first backfill imports the entire history; the default bounds the
    ///     initial window to <see cref="DefaultInitialSyncFloor"/> so a first sync against a
    ///     long-running source is not unbounded.
    /// </summary>
    protected virtual DateTime? InitialSyncFloor => DefaultInitialSyncFloor();

    /// <summary>The default initial backfill window: six months before now.</summary>
    protected static DateTime DefaultInitialSyncFloor() => DateTime.UtcNow.AddMonths(-6);

    /// <summary>
    ///     Core synchronization logic: fetches and publishes the data types
    ///     <see cref="ResolveActiveTypes"/> resolves for the run. Shared between the manual and
    ///     background sync flows. There is deliberately no default implementation: a connector that
    ///     advertises data types it does not sync would fail silently, so the omission is a compile
    ///     error instead.
    /// </summary>
    protected abstract Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken);

    /// <summary>
    ///     The data types one run of <see cref="PerformSyncInternalAsync"/> may touch: what the
    ///     caller asked for, narrowed to what the tenant has switched on. An empty
    ///     <see cref="SyncRequest.DataTypes"/> asks for everything, which is how an unfiltered cursor
    ///     reset arrives; a narrowed one is an operator re-pulling a single type and must not drag
    ///     the rest of the connector's history back with it.
    /// </summary>
    /// <remarks>
    ///     Answered rather than written back into <paramref name="request"/>: a tenant-wide cursor
    ///     reset builds one <see cref="SyncRequest"/> and hands the same instance to every connector
    ///     it fans out to, so a connector recording its own answer there would narrow the next
    ///     connector's run to its own supported types.
    /// </remarks>
    protected HashSet<SyncDataType> ResolveActiveTypes(SyncRequest request, TConfig config)
    {
        var enabled = config.GetEnabledDataTypes(SupportedDataTypes).ToHashSet();
        return request.DataTypes.Count == 0 ? enabled : [.. request.DataTypes.Where(enabled.Contains)];
    }

    protected virtual Task<IEnumerable<Profile>> FetchProfilesAsync()
    {
        return Task.FromResult(Enumerable.Empty<Profile>());
    }

    /// <summary>
    ///     Submits glucose data directly to the API via HTTP
    /// </summary>
    /// <summary>
    ///     The broadcast origin for this run's glucose-family publishes: <see cref="WriteOrigin.Backfill"/>
    ///     on the source's first-ever glucose sync (no prior data — suppress so a first sync of history
    ///     doesn't flood clients), else <see cref="WriteOrigin.Live"/>. Memoized for the run.
    /// </summary>
    protected async Task<WriteOrigin> GlucosePublishOriginAsync()
    {
        _glucosePublishOrigin ??= await ResolvePublishOriginAsync(
            () => _publisher!.Glucose.GetLatestEntryTimestampAsync(ConnectorSource));
        return _glucosePublishOrigin.Value;
    }

    /// <summary>
    ///     The broadcast origin for this run's care-family (treatment) publishes — Bolus, CarbIntake,
    ///     BG check, calculations, basal, notes, device events. Backfill on the source's first-ever
    ///     treatment sync, else Live. Memoized for the run.
    /// </summary>
    protected async Task<WriteOrigin> TreatmentPublishOriginAsync()
    {
        _treatmentPublishOrigin ??= await ResolvePublishOriginAsync(
            () => _publisher!.Treatments.GetLatestTreatmentTimestampAsync(ConnectorSource));
        return _treatmentPublishOrigin.Value;
    }

    /// <summary>
    ///     The broadcast origin for this run's device-status (snapshot) publishes — APS, pump, and uploader
    ///     snapshots. Backfill on the source's first-ever device-status sync (suppress so a first sync of
    ///     history doesn't flood the device category), else Live. Memoized for the run.
    /// </summary>
    protected async Task<WriteOrigin> DevicePublishOriginAsync()
    {
        _devicePublishOrigin ??= await ResolvePublishOriginAsync(
            () => _publisher!.Device.GetLatestDeviceStatusTimestampAsync(ConnectorSource));
        return _devicePublishOrigin.Value;
    }

    /// <summary>
    ///     Resolves a publish origin from a resume watermark: Backfill when no prior data exists (initial
    ///     full-history sync), else Live. When the publisher is unavailable the publish will fail anyway,
    ///     so the origin is irrelevant and defaults to Live.
    /// </summary>
    private async Task<WriteOrigin> ResolvePublishOriginAsync(Func<Task<DateTime?>> latestTimestamp)
    {
        if (_publisher is not { IsAvailable: true })
            return WriteOrigin.Live;
        return await latestTimestamp() is null ? WriteOrigin.Backfill : WriteOrigin.Live;
    }

    protected virtual async Task<bool> PublishGlucoseDataAsync(
        IEnumerable<Entry> entries,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for glucose data submission");
            return false;
        }

        return await _publisher.Glucose.PublishEntriesAsync(entries, ConnectorSource, await GlucosePublishOriginAsync(), cancellationToken);
    }

    /// <summary>
    ///     Submits treatment data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishTreatmentDataAsync(
        IEnumerable<Treatment> treatments,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for treatment data submission");
            return false;
        }

        return await _publisher.Treatments.PublishTreatmentsAsync(
            treatments,
            ConnectorSource, await TreatmentPublishOriginAsync(),
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits device status data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for device status submission");
            return false;
        }

        return await _publisher.Device.PublishDeviceStatusAsync(
            deviceStatuses,
            ConnectorSource, await DevicePublishOriginAsync(),
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits profile data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishProfileDataAsync(
        IEnumerable<Profile> profiles,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for profile data submission");
            return false;
        }

        return await _publisher.Metadata.PublishProfilesAsync(profiles, ConnectorSource, WriteOrigin.Live, cancellationToken); // Dormant broadcast category (snapshots off-base / no V4 category yet) — origin irrelevant until wired.
    }

    /// <summary>
    ///     Submits food data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishFoodDataAsync(
        IEnumerable<Food> foods,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for food data submission");
            return false;
        }

        return await _publisher.Metadata.PublishFoodAsync(foods, ConnectorSource, WriteOrigin.Live, cancellationToken); // Dormant broadcast category (snapshots off-base / no V4 category yet) — origin irrelevant until wired.
    }

    /// <summary>
    ///     Submits activity data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishActivityDataAsync(
        IEnumerable<Activity> activities,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for activity data submission");
            return false;
        }

        return await _publisher.Metadata.PublishActivityAsync(
            activities,
            ConnectorSource, WriteOrigin.Live,
            cancellationToken
        ); // Dormant broadcast category (snapshots off-base / no V4 category yet) — origin irrelevant until wired.
    }

    /// <summary>
    ///     Submits state span data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishStateSpanDataAsync(
        IEnumerable<StateSpan> stateSpans,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for state span submission");
            return false;
        }

        return await _publisher.Metadata.PublishStateSpansAsync(
            stateSpans,
            ConnectorSource, WriteOrigin.Live,
            cancellationToken
        ); // Dormant broadcast category (snapshots off-base / no V4 category yet) — origin irrelevant until wired.
    }

    /// <summary>
    ///     Submits system event data directly to the API via HTTP. System events have no
    ///     <see cref="SyncDataType"/> of their own, so a connector routing them through
    ///     <see cref="PublishRecordTypeAsync{T}"/> gates and counts them under
    ///     <see cref="SyncDataType.DeviceEvents"/>.
    /// </summary>
    protected virtual async Task<bool> PublishSystemEventDataAsync(
        IEnumerable<SystemEvent> systemEvents,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for system event submission");
            return false;
        }

        return await _publisher.Metadata.PublishSystemEventsAsync(
            systemEvents,
            ConnectorSource, WriteOrigin.Live,
            cancellationToken
        ); // Dormant broadcast category (snapshots off-base / no V4 category yet) — origin irrelevant until wired.
    }

    /// <summary>
    ///     Reusable helper that checks whether a data type is active, reports publish progress,
    ///     publishes a batch of records, and records the outcome through
    ///     <see cref="RecordPublishOutcome"/>.
    /// </summary>
    /// <param name="context">
    ///     Detail about this batch — where it came from, or what it held — appended to the success log
    ///     in parentheses.
    /// </param>
    /// <returns>
    ///     Whether the batch reached the tenant. An inactive type, an empty batch and a rejected
    ///     publish are alike <c>false</c>: no record was accepted in any of them.
    /// </returns>
    protected async Task<bool> PublishRecordTypeAsync<T>(
        SyncResult result,
        SyncDataType dataType,
        HashSet<SyncDataType> activeTypes,
        List<T> records,
        Func<List<T>, TConfig, CancellationToken, Task<bool>> publishFunc,
        TConfig config,
        CancellationToken cancellationToken,
        string? context = null) where T : class
    {
        if (!activeTypes.Contains(dataType)) return false;

        if (records.Count == 0)
        {
            RecordPublishOutcome(result, dataType, 0, success: true);
            return false;
        }

        await ReportSyncMessageAsync(SyncMessageType.PublishingDataType,
            new() { ["count"] = records.Count.ToString(), ["dataType"] = dataType.ToString() },
            cancellationToken);

        var success = await publishFunc(records, config, cancellationToken);
        RecordPublishOutcome(result, dataType, records.Count, success, context);
        return success;
    }

    /// <summary>
    ///     The bookkeeping every published record type owes the run: the per-type count the tenant's
    ///     sync card renders, the canonical failure string, and the success log. Connectors whose
    ///     publish shape does not fit <see cref="PublishRecordTypeAsync{T}"/> — a streaming crawl that
    ///     publishes page by page, or one fetch feeding several types — record through this instead of
    ///     writing the three by hand.
    /// </summary>
    /// <remarks>
    ///     A type the sync looked at records a count even when it came back empty: the card renders a
    ///     badge per key, so a missing key reads as "never checked" rather than "checked, found
    ///     nothing". Counts accumulate, so a paginated crawl can report each page and a later empty
    ///     page cannot erase what an earlier one landed. Callers report the count once the publish has
    ///     returned, so a publish that throws records nothing while one that reports failure records
    ///     the batch it handed over — the count is what reached the publisher, not what the publisher
    ///     accepted.
    /// </remarks>
    /// <param name="context">
    ///     Detail about this batch — where it came from, or what it held — appended to the success log
    ///     in parentheses.
    /// </param>
    protected void RecordPublishOutcome(
        SyncResult result,
        SyncDataType dataType,
        int count,
        bool success,
        string? context = null)
    {
        result.ItemsSynced.TryGetValue(dataType, out var previous);
        result.ItemsSynced[dataType] = previous + count;

        if (!success)
        {
            RecordFailure(result, $"{dataType} publish failed", PublishFailedMessage);
        }
        else if (count > 0)
        {
            _logger.LogInformation("[{ConnectorSource}] Synced {Count} {Type} records{Context}",
                ConnectorSource, count, dataType, context != null ? $" ({context})" : "");
        }
    }

    /// <summary>
    ///     The counterpart of <see cref="RecordPublishOutcome"/> one level up: a fetch that came back
    ///     with nothing because it failed, reported as a failure of the run but only for a type the
    ///     tenant enabled. Records no count — the source was never reached, and a count is a claim it
    ///     was (see the remarks on <see cref="RecordPublishOutcome"/>).
    /// </summary>
    /// <remarks>
    ///     A failed run withholds the connector's last-successful-sync stamp and shows the tenant a
    ///     red connector, so a fetch issued only to support another type — a bolus fetch feeding a
    ///     carb correlation — must not be able to fail the sync. Losing it costs that correlation and
    ///     nothing else. The failure is sticky rather than fatal: whatever the run already fetched
    ///     still publishes.
    /// </remarks>
    protected void RecordFetchFailure(
        SyncResult result,
        SyncDataType dataType,
        HashSet<SyncDataType> activeTypes)
    {
        if (!activeTypes.Contains(dataType))
        {
            _logger.LogDebug(
                "[{ConnectorSource}] {DataType} fetch failed for a type that is switched off",
                ConnectorSource, dataType);
            return;
        }

        RecordFailure(result, $"Failed to fetch {dataType}", FetchFailedMessage);
    }

    /// <summary>
    ///     The failure a connector raises when the source did not answer, for the fetches whose
    ///     caller cannot tell a failure from a result by its shape.
    /// </summary>
    /// <remarks>
    ///     A fetch that failed is not an answer, and must never be read as one. Standing in an empty
    ///     result ends a paginated crawl at the page that broke and reports the truncation as a
    ///     successful sync, and it advances whatever the connector resumes from next cycle — a
    ///     persisted backfill low-water mark, or the newest record now stored locally — past history
    ///     that was never read, so the gap outlives the failure instead of being repaired. A source
    ///     with nothing left to give answers with an empty payload; that is what ends a crawl.
    /// </remarks>
    /// <param name="detail">
    ///     What the source did, when the caller knows it and the reader would otherwise have to read
    ///     the connector logs to find out — which a hosted tenant cannot do.
    /// </param>
    protected static Exception FetchFailed(string operationName, string? detail = null) =>
        new HttpRequestException(detail is null
            ? $"{operationName} fetch failed; see preceding connector logs"
            : $"{operationName} fetch failed: {detail}");

    /// <summary>
    ///     What a run says for itself when it failed and the reader has no <see cref="SyncResult.Errors"/>
    ///     to go on. The first recorded failure names the run, so a fetch that fell over followed by a
    ///     publish rejection still reads as the fetch failure that started it.
    /// </summary>
    protected const string FetchFailedMessage = "Sync failed while fetching data";

    /// <inheritdoc cref="FetchFailedMessage"/>
    protected const string PublishFailedMessage = "Sync failed while publishing data";

    private static void RecordFailure(SyncResult result, string error, string message)
    {
        if (result.Errors.Count == 0)
            result.Message = message;

        result.Success = false;

        // A windowed sync meets the same failure once per chunk, and the terminal progress message
        // joins every entry, so the tenant reads one line per distinct failure rather than per chunk.
        if (!result.Errors.Contains(error))
            result.Errors.Add(error);
    }

    /// <summary>
    ///     Reports a sync-progress message to the reporter supplied for this run, if any. The
    ///     message type carries the phase, so a terminal message cannot be emitted as in-progress.
    /// </summary>
    protected Task ReportSyncMessageAsync(
        SyncMessageType messageType,
        Dictionary<string, string>? messageParams,
        CancellationToken cancellationToken,
        string? errorMessage = null)
    {
        if (_progressReporter is null) return Task.CompletedTask;

        return _progressReporter.ReportProgressAsync(new SyncProgressEvent
        {
            ConnectorId = ConnectorSource,
            ConnectorName = ServiceName,
            Phase = PhaseOf(messageType),
            ErrorMessage = errorMessage,
            MessageType = messageType,
            MessageParams = messageParams,
        }, cancellationToken);
    }

    private static SyncPhase PhaseOf(SyncMessageType messageType) => messageType switch
    {
        SyncMessageType.SyncComplete => SyncPhase.Completed,
        SyncMessageType.SyncFailed => SyncPhase.Failed,
        _ => SyncPhase.Syncing,
    };

    #region V4 Publishing Methods

    /// <summary>
    ///     Submits V4 SensorGlucose data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishSensorGlucoseDataAsync(
        IEnumerable<SensorGlucose> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for SensorGlucose submission");
            return false;
        }

        // Stamp glucose processing metadata from connector config
        var processing = config.GlucoseProcessing;
        foreach (var record in records)
        {
            record.GlucoseProcessing = processing;
            record.SmoothedMgdl ??= processing == GlucoseProcessing.Smoothed ? record.Mgdl : null;
        }

        return await _publisher.Glucose.PublishSensorGlucoseAsync(
            records,
            ConnectorSource, await GlucosePublishOriginAsync(),
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits V4 Bolus data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishBolusDataAsync(
        IEnumerable<Bolus> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for Bolus submission");
            return false;
        }

        return await _publisher.Treatments.PublishBolusesAsync(records, ConnectorSource, await TreatmentPublishOriginAsync(), cancellationToken);
    }

    /// <summary>
    ///     Submits V4 CarbIntake data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishCarbIntakeDataAsync(
        IEnumerable<CarbIntake> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for CarbIntake submission");
            return false;
        }

        return await _publisher.Treatments.PublishCarbIntakesAsync(
            records,
            ConnectorSource, await TreatmentPublishOriginAsync(),
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits V4 BGCheck data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishBGCheckDataAsync(
        IEnumerable<BGCheck> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for BGCheck submission");
            return false;
        }

        return await _publisher.Treatments.PublishBGChecksAsync(records, ConnectorSource, await TreatmentPublishOriginAsync(), cancellationToken);
    }

    /// <summary>
    ///     Submits V4 BolusCalculation data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishBolusCalculationDataAsync(
        IEnumerable<BolusCalculation> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for BolusCalculation submission");
            return false;
        }

        return await _publisher.Treatments.PublishBolusCalculationsAsync(
            records,
            ConnectorSource, await TreatmentPublishOriginAsync(),
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits V4 Note data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishNoteDataAsync(
        IEnumerable<Note> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for Note submission");
            return false;
        }

        return await _publisher.Metadata.PublishNotesAsync(records, ConnectorSource, await TreatmentPublishOriginAsync(), cancellationToken);
    }

    /// <summary>
    ///     Submits V4 DeviceEvent data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishDeviceEventDataAsync(
        IEnumerable<DeviceEvent> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for DeviceEvent submission");
            return false;
        }

        return await _publisher.Device.PublishDeviceEventsAsync(
            records,
            ConnectorSource, await TreatmentPublishOriginAsync(),
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits V4 TempBasal data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishTempBasalDataAsync(
        IEnumerable<TempBasal> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for TempBasal submission");
            return false;
        }

        return await _publisher.Treatments.PublishTempBasalsAsync(
            records,
            ConnectorSource, await TreatmentPublishOriginAsync(),
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits V4 BasalInjection data directly to the API
    /// </summary>
    protected virtual async Task<bool> PublishBasalInjectionDataAsync(
        IEnumerable<BasalInjection> records,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for BasalInjection submission");
            return false;
        }

        return await _publisher.Treatments.PublishBasalInjectionsAsync(
            records,
            ConnectorSource, await TreatmentPublishOriginAsync(),
            cancellationToken
        );
    }

    #endregion

    /// <summary>
    ///     Publishes messages in batches to optimize throughput
    /// </summary>
    protected virtual async Task<bool> PublishGlucoseDataInBatchesAsync(
        IEnumerable<Entry> entries,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        var entriesArray = entries.ToArray();
        if (entriesArray.Length == 0)
            return true;

        var batchSize = Math.Max(1, config.BatchSize);
        var batches = entriesArray
            .Select((entry, index) => new { entry, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.entry).ToArray());

        var allSuccessful = true;
        var batchNumber = 1;

        foreach (var batch in batches)
        {
            _logger?.LogDebug(
                "Publishing batch {BatchNumber} with {Count} entries",
                batchNumber,
                batch.Length
            );

            var success = await PublishGlucoseDataAsync(batch, config, cancellationToken);
            if (!success)
            {
                allSuccessful = false;
                _logger?.LogWarning("Failed to publish batch {BatchNumber}", batchNumber);
            }

            batchNumber++;

            // Small delay between batches to avoid overwhelming the message bus
            if (batchNumber > 1)
                await Task.Delay(10, cancellationToken);
        }

        return allSuccessful;
    }

    /// <summary>
    ///     Publishes treatment messages in batches to optimize throughput
    /// </summary>
    protected virtual async Task<bool> PublishTreatmentDataInBatchesAsync(
        IEnumerable<Treatment> treatments,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        var treatmentsArray = treatments.ToArray();
        if (treatmentsArray.Length == 0)
            return true;

        var batchSize = Math.Max(1, config.BatchSize);
        var batches = treatmentsArray
            .Select((treatment, index) => new { treatment, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.treatment).ToArray());

        var allSuccessful = true;
        var batchNumber = 1;

        foreach (var batch in batches)
        {
            _logger?.LogDebug(
                "Publishing treatment batch {BatchNumber} with {Count} entries",
                batchNumber,
                batch.Length
            );

            var success = await PublishTreatmentDataAsync(batch, config, cancellationToken);
            if (!success)
            {
                allSuccessful = false;
                _logger?.LogWarning("Failed to publish treatment batch {BatchNumber}", batchNumber);
            }

            batchNumber++;

            // Small delay between batches to avoid overwhelming the message bus
            if (batchNumber > 1)
                await Task.Delay(10, cancellationToken);
        }

        return allSuccessful;
    }

    /// <summary>
    ///     Main sync method that handles data synchronization based on connector mode
    /// </summary>
    /// <summary>
    ///     Main sync method for background synchronization.
    ///     Uses PerformSyncInternalAsync for sequential processing.
    /// </summary>
    public virtual Task<SyncResult> SyncDataAsync(
        TConfig config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null
    ) =>
        RunWithProgressAsync(
            progressReporter,
            cancellationToken,
            () => RunBackgroundSyncAsync(config, cancellationToken, since));

    private async Task<SyncResult> RunBackgroundSyncAsync(
        TConfig config,
        CancellationToken cancellationToken,
        DateTime? since
    )
    {
        _logger.LogInformation(
            "Starting background data sync for {ConnectorSource}",
            ConnectorSource
        );
        try
        {
            // Authenticate if needed
            if (!await AuthenticateAsync())
            {
                _logger.LogError("Authentication failed for {ConnectorSource}", ConnectorSource);
                return AuthenticationFailedResult();
            }

            // Determine catch-up timestamp
            var sinceTimestamp = since ?? await CalculateSinceTimestampAsync(config);

            var request = new SyncRequest
            {
                From = sinceTimestamp,
                To = null, // Open-ended for background sync
                DataTypes = SupportedDataTypes,
            };

            var result = await PerformSyncInternalAsync(request, config, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Background sync completed successfully for {ConnectorSource}",
                    ConnectorSource
                );

                // Log details of what was synced
                foreach (var type in result.ItemsSynced.Keys)
                    if (result.ItemsSynced[type] > 0)
                        _logger.LogInformation(
                            "Synced {Count} {Type} items",
                            result.ItemsSynced[type],
                            type
                        );
            }
            else
            {
                _logger.LogError(
                    "Background sync for {ConnectorSource} failed or had errors: {Errors}",
                    ConnectorSource,
                    string.Join("; ", result.Errors)
                );
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error in background SyncDataAsync for {ConnectorSource}",
                ConnectorSource
            );
            return new SyncResult
            {
                Success = false,
                StartTime = DateTimeOffset.UtcNow,
                EndTime = DateTimeOffset.UtcNow,
                Errors = { ex.Message }
            };
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        // HttpClient is managed by IHttpClientFactory - do not dispose
    }

    #region Health Tracking

    /// <summary>
    ///     Tracks consecutive failed requests for health monitoring.
    ///     Automatically incremented on failures and reset on success.
    /// </summary>
    private int _failedRequestCount;

    /// <summary>
    ///     Maximum failed requests before connector is considered unhealthy.
    ///     Override in derived classes to customize threshold.
    /// </summary>
    protected virtual int MaxFailedRequestsBeforeUnhealthy => 5;

    /// <summary>
    ///     Gets whether the connector is in a healthy state based on recent request failures.
    ///     Returns false if consecutive failures exceed MaxFailedRequestsBeforeUnhealthy.
    /// </summary>
    public virtual bool IsHealthy =>
        Volatile.Read(ref _failedRequestCount) < MaxFailedRequestsBeforeUnhealthy;

    /// <summary>
    ///     Gets the number of consecutive failed requests.
    /// </summary>
    public int FailedRequestCount => Volatile.Read(ref _failedRequestCount);

    /// <summary>
    ///     Resets the failed request counter. Call this after successful recovery.
    /// </summary>
    public virtual void ResetFailedRequestCount()
    {
        Interlocked.Exchange(ref _failedRequestCount, 0);
        _logger.LogInformation("[{ConnectorSource}] Failed request count reset", ConnectorSource);
    }

    /// <summary>
    ///     Increments the failed request count and logs the failure.
    /// </summary>
    protected void TrackFailedRequest(string? reason = null)
    {
        var newCount = Interlocked.Increment(ref _failedRequestCount);
        _logger.LogWarning(
            "[{ConnectorSource}] Request failed (count: {FailedCount}/{MaxAllowed}){Reason}",
            ConnectorSource,
            newCount,
            MaxFailedRequestsBeforeUnhealthy,
            reason != null ? $": {reason}" : ""
        );
    }

    /// <summary>
    ///     Resets the failed request count on success.
    /// </summary>
    protected void TrackSuccessfulRequest()
    {
        var previousCount = Volatile.Read(ref _failedRequestCount);
        if (previousCount > 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] Request succeeded, resetting failed count from {PreviousCount}",
                ConnectorSource,
                previousCount
            );
            Interlocked.Exchange(ref _failedRequestCount, 0);
        }
    }

    #endregion

    #region Retry and HTTP Helpers

    /// <summary>
    ///     Executes an async operation under the shared connector retry loop, tracking success and
    ///     failure for health monitoring. See <see cref="ConnectorRetryLoop.RunAsync{T}"/> for the
    ///     attempt-budget and delay contract.
    /// </summary>
    /// <typeparam name="T">The return type of the operation</typeparam>
    /// <param name="operation">The async operation to execute</param>
    /// <param name="retryStrategy">Strategy for calculating retry delays</param>
    /// <param name="reAuthenticateOnUnauthorized">Optional callback to re-authenticate on 401 responses</param>
    /// <param name="maxRetries">Total attempts, not retries on top of a first try; clamped to a floor of one (default: 3).</param>
    /// <param name="operationName">Name of the operation for logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the operation, or default(T) on failure</returns>
    protected async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<T?>> operation,
        IRetryDelayStrategy retryStrategy,
        Func<Task<bool>>? reAuthenticateOnUnauthorized = null,
        int maxRetries = 3,
        string? operationName = null,
        CancellationToken cancellationToken = default
    )
    {
        var opName = operationName ?? "operation";
        HttpRequestException? lastException = null;

        return await ConnectorRetryLoop.RunAsync<T>(
            async (attempt, attempts) =>
            {
                try
                {
                    _logger.LogDebug(
                        "[{ConnectorSource}] Executing {Operation} (attempt {Attempt}/{MaxRetries})",
                        ConnectorSource,
                        opName,
                        attempt + 1,
                        attempts
                    );

                    var result = await operation();

                    TrackSuccessfulRequest();
                    return RetryStep<T>.Complete(result);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning(
                        "[{ConnectorSource}] Unauthorized response during {Operation}, attempting re-authentication",
                        ConnectorSource,
                        opName
                    );

                    if (reAuthenticateOnUnauthorized != null)
                    {
                        var reAuthSuccess = await reAuthenticateOnUnauthorized();
                        if (reAuthSuccess)
                        {
                            _logger.LogInformation(
                                "[{ConnectorSource}] Re-authentication successful, retrying {Operation}",
                                ConnectorSource,
                                opName
                            );
                            return RetryStep<T>.RetryImmediately;
                        }
                    }

                    TrackFailedRequest("Unauthorized and re-authentication failed");
                    return RetryStep<T>.Complete(default);
                }
                catch (HttpRequestException ex) when (HttpResponseExtensions.IsRetryableStatusCode(ex.StatusCode))
                {
                    lastException = ex;
                    _logger.LogWarning(
                        "[{ConnectorSource}] Retryable error during {Operation} (attempt {Attempt}): {StatusCode}",
                        ConnectorSource,
                        opName,
                        attempt + 1,
                        ex.StatusCode
                    );

                    return RetryStep<T>.RetryAfterDelay;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(
                        ex,
                        "[{ConnectorSource}] Non-retryable HTTP error during {Operation}: {StatusCode}",
                        ConnectorSource,
                        opName,
                        ex.StatusCode
                    );
                    TrackFailedRequest($"HTTP {ex.StatusCode}");
                    return RetryStep<T>.Complete(default);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(
                        ex,
                        "[{ConnectorSource}] JSON parsing error during {Operation}",
                        ConnectorSource,
                        opName
                    );
                    TrackFailedRequest("JSON parsing error");
                    return RetryStep<T>.Complete(default);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation(
                        "[{ConnectorSource}] {Operation} was cancelled",
                        ConnectorSource,
                        opName
                    );
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[{ConnectorSource}] Unexpected error during {Operation}",
                        ConnectorSource,
                        opName
                    );
                    TrackFailedRequest($"Unexpected error: {ex.Message}");
                    return RetryStep<T>.Complete(default);
                }
            },
            retryStrategy,
            maxRetries,
            attempts =>
            {
                TrackFailedRequest($"All {attempts} attempts failed");
                _logger.LogError(
                    "[{ConnectorSource}] {Operation} failed after {MaxRetries} attempts",
                    ConnectorSource,
                    opName,
                    attempts
                );

                if (lastException != null)
                    throw lastException;

                return default;
            },
            cancellationToken
        );
    }

    /// <summary>
    ///     Sends an HTTP request with optional custom headers.
    ///     Useful for APIs that require per-request headers like Account-Id.
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="url">Request URL</param>
    /// <param name="additionalHeaders">Optional headers to add to the request</param>
    /// <param name="content">Optional request content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>HTTP response message</returns>
    protected async Task<HttpResponseMessage> SendWithHeadersAsync(
        HttpMethod method,
        string url,
        Dictionary<string, string>? additionalHeaders = null,
        HttpContent? content = null,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(method, url);

        if (additionalHeaders != null)
            foreach (var header in additionalHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (content != null)
            request.Content = content;

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    ///     Sends a GET request with optional custom headers.
    /// </summary>
    protected Task<HttpResponseMessage> GetWithHeadersAsync(
        string url,
        Dictionary<string, string>? additionalHeaders = null,
        CancellationToken cancellationToken = default
    )
    {
        return SendWithHeadersAsync(
            HttpMethod.Get,
            url,
            additionalHeaders,
            null,
            cancellationToken
        );
    }

    /// <summary>
    ///     Sends a POST request with optional custom headers and content.
    /// </summary>
    protected Task<HttpResponseMessage> PostWithHeadersAsync(
        string url,
        HttpContent? content = null,
        Dictionary<string, string>? additionalHeaders = null,
        CancellationToken cancellationToken = default
    )
    {
        return SendWithHeadersAsync(
            HttpMethod.Post,
            url,
            additionalHeaders,
            content,
            cancellationToken
        );
    }

    /// <summary>
    ///     Deserializes JSON content from an HTTP response using case-insensitive options.
    /// </summary>
    protected async Task<T?> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default
    )
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content, JsonDefaults.CaseInsensitive);
    }

    #endregion
}
