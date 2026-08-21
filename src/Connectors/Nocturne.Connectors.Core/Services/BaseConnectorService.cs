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

    public abstract Task<bool> AuthenticateAsync();

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
    ///     Core synchronization logic: fetches and publishes every data type the tenant has enabled,
    ///     honouring <see cref="BaseConnectorConfiguration.GetEnabledDataTypes"/> over
    ///     <see cref="SupportedDataTypes"/>. Shared between the manual and background sync flows.
    ///     There is deliberately no default implementation: a connector that advertises data types it
    ///     does not sync would fail silently, so the omission is a compile error instead.
    /// </summary>
    protected abstract Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken);

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
    ///     publishes a batch of records, updates the <see cref="SyncResult"/> counts, and logs the
    ///     outcome.
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
            // An active type the sync did look at reports an explicit zero: the tenant's sync card
            // renders a badge per key, so a missing key reads as "never checked" rather than
            // "checked, found nothing". TryAdd so a later empty page cannot erase an earlier count.
            result.ItemsSynced.TryAdd(dataType, 0);
            return false;
        }

        await ReportSyncMessageAsync(SyncMessageType.PublishingDataType,
            new() { ["count"] = records.Count.ToString(), ["dataType"] = dataType.ToString() },
            cancellationToken);

        var success = await publishFunc(records, config, cancellationToken);
        result.ItemsSynced.TryGetValue(dataType, out var prev);
        result.ItemsSynced[dataType] = prev + records.Count;
        if (!success)
        {
            result.Success = false;
            result.Errors.Add($"{dataType} publish failed");
        }
        else
        {
            _logger.LogInformation("[{ConnectorSource}] Synced {Count} {Type} records{Context}",
                ConnectorSource, records.Count, dataType, context != null ? $" ({context})" : "");
        }

        return success;
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
    ///     Executes an async operation with retry logic and exponential backoff.
    ///     Automatically tracks success/failure for health monitoring.
    /// </summary>
    /// <typeparam name="T">The return type of the operation</typeparam>
    /// <param name="operation">The async operation to execute</param>
    /// <param name="retryStrategy">Strategy for calculating retry delays</param>
    /// <param name="reAuthenticateOnUnauthorized">Optional callback to re-authenticate on 401 responses</param>
    /// <param name="maxRetries">
    ///     Maximum number of attempts (default: 3). Clamped to a floor of 1 so a connector's
    ///     configured MaxRetryAttempts of 0 still makes a single attempt instead of skipping
    ///     the operation entirely.
    /// </param>
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
        // Connectors pass their configured MaxRetryAttempts, which allows 0; the loop needs at
        // least one attempt for the operation to run.
        maxRetries = Math.Max(1, maxRetries);

        var opName = operationName ?? "operation";
        HttpRequestException? lastException = null;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogDebug(
                    "[{ConnectorSource}] Executing {Operation} (attempt {Attempt}/{MaxRetries})",
                    ConnectorSource,
                    opName,
                    attempt + 1,
                    maxRetries
                );

                var result = await operation();

                // Success - track it and return
                TrackSuccessfulRequest();
                return result;
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
                        continue; // Retry with new credentials
                    }
                }

                TrackFailedRequest("Unauthorized and re-authentication failed");
                return default;
            }
            catch (HttpRequestException ex) when (IsRetryableStatusCode(ex.StatusCode))
            {
                lastException = ex;
                _logger.LogWarning(
                    "[{ConnectorSource}] Retryable error during {Operation} (attempt {Attempt}): {StatusCode}",
                    ConnectorSource,
                    opName,
                    attempt + 1,
                    ex.StatusCode
                );

                if (attempt < maxRetries - 1)
                    await retryStrategy.ApplyRetryDelayAsync(attempt);
            }
            catch (HttpRequestException ex)
            {
                // Non-retryable HTTP error
                _logger.LogError(
                    ex,
                    "[{ConnectorSource}] Non-retryable HTTP error during {Operation}: {StatusCode}",
                    ConnectorSource,
                    opName,
                    ex.StatusCode
                );
                TrackFailedRequest($"HTTP {ex.StatusCode}");
                return default;
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
                return default;
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
                return default;
            }
        }

        // All retries exhausted
        TrackFailedRequest($"All {maxRetries} attempts failed");
        _logger.LogError(
            "[{ConnectorSource}] {Operation} failed after {MaxRetries} attempts",
            ConnectorSource,
            opName,
            maxRetries
        );

        if (lastException != null)
            throw lastException;

        return default;
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
    ///     Determines if an HTTP status code is retryable.
    /// </summary>
    private static bool IsRetryableStatusCode(HttpStatusCode? statusCode)
    {
        return statusCode
            is HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.GatewayTimeout
                or HttpStatusCode.RequestTimeout;
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
