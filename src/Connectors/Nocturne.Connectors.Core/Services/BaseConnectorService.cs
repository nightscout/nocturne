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

public abstract partial class BaseConnectorService<TConfig> : IConnectorService<TConfig>
    where TConfig : BaseConnectorConfiguration
{
    protected readonly HttpClient _httpClient;
    protected readonly IConnectorServerResolver<TConfig> _serverResolver;
    protected readonly ILogger _logger;
    private readonly IConnectorPublisher? _publisher;

    /// <summary>The only clock derived connectors read, so a test can drive it.</summary>
    protected TimeProvider Clock { get; }

    /// <summary>
    ///     The delay between attempts of <see cref="ExecuteWithRetryAsync{T}"/>, for a connector that
    ///     runs <see cref="ConnectorRetryLoop"/> itself.
    /// </summary>
    protected IRetryDelayStrategy RetryDelayStrategy { get; }

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

    /// <param name="httpClient">Owned by IHttpClientFactory; never disposed here.</param>
    /// <param name="publisher"><c>null</c> when running detached, e.g. dry-run tooling.</param>
    protected BaseConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TConfig> serverResolver,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serverResolver = serverResolver ?? throw new ArgumentNullException(nameof(serverResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        RetryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _publisher = publisher;
        Clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     The data source id every record this connector imports is stored under, and that its
    ///     resume watermarks are read by: the <see cref="ConnectorRegistrationAttribute.DataSourceId"/>
    ///     on <typeparamref name="TConfig"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <typeparamref name="TConfig"/> declares no data source id, so nothing the connector
    ///     stores could be found again.
    /// </exception>
    protected virtual string ConnectorSource =>
        Registration?.DataSourceId is { Length: > 0 } dataSourceId
            ? dataSourceId
            : throw new InvalidOperationException(
                $"{typeof(TConfig).Name} declares no {nameof(ConnectorRegistrationAttribute.DataSourceId)}.");

    public abstract string ServiceName { get; }

    /// <summary>
    /// The data types this connector fetches, read from the <see cref="ConnectorRegistrationAttribute"/>
    /// on <typeparamref name="TConfig"/>. The attribute drives the tenant-facing toggle schema and
    /// this property drives the sync loop, so stating them separately lets a connector advertise a
    /// toggle it never acts on, or act on data the tenant has no way to turn off.
    /// </summary>
    public virtual List<SyncDataType> SupportedDataTypes => [.. Registration?.SupportedDataTypes ?? [SyncDataType.Glucose]];

    /// <remarks>
    ///     Not <see cref="ConnectorRegistrationAttribute.DeclaredOn"/>: a connector service may be
    ///     closed over a configuration carrying no registration at all, whose data types default
    ///     to glucose rather than failing. The inherit rule is the same one.
    /// </remarks>
    private static readonly ConnectorRegistrationAttribute? Registration =
        typeof(TConfig).GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false);

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
    /// <remarks>
    ///     A reason recorded through <see cref="TrackFailedAuthentication"/> names the failure
    ///     instead. A connector that never reached its source has no credential to fix; telling
    ///     that person their secret was rejected sends them after the wrong thing.
    /// </remarks>
    protected SyncResult AuthenticationFailedResult()
    {
        return new SyncResult
        {
            Success = false,
            Message = _authenticationFailureReason ?? "Authentication failed",
            Errors = { _authenticationFailureReason ?? $"Authentication failed for {ConnectorSource}" },
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
        _marks = null;
        var skippedBefore = _publisher?.SkippedDeleted ?? 0;
        var refusedBefore = _publisher?.Refused ?? 0;
        try
        {
            var result = await body();
            result.ItemsSkipped = (_publisher?.SkippedDeleted ?? 0) - skippedBefore;
            result.ItemsRefused = (_publisher?.Refused ?? 0) - refusedBefore;
            StandInFailureMessage(result);
            await ReportSyncOutcomeAsync(result.Success, FailureMessage(result), cancellationToken);
            return result;
        }
        // A run the caller withdrew still resolves the tenant's in-progress indicator: report one
        // reasonless terminal failure under CancellationToken.None, then let the cancellation travel.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReportSyncOutcomeAsync(false, null, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
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

    private async Task<DateTime?> FetchLatestEntryTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest entry timestamp"
            );
            return null;
        }

        // A failed read propagates rather than reading as null: a null watermark means "no prior
        // data", which triggers an initial backfill, unbounded for connectors with a null
        // InitialSyncFloor. A transient read failure must fail this cycle (retried next interval),
        // not be amplified into a full-history recrawl and republish.
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

    private async Task<DateTime?> FetchLatestTreatmentTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest treatment timestamp"
            );
            return null;
        }

        // A failed read propagates for the reason given in FetchLatestEntryTimestampAsync.
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
    ///     The glucose family's resume point: the most recent stored entry (minus the catch-up
    ///     overlap), or <see cref="InitialSyncFloor"/> when none is stored. Combine it with a
    ///     caller's bound through <see cref="ResumeFrom(DateTime?, DateTime?)"/> rather than
    ///     choosing between the two.
    /// </summary>
    protected async Task<DateTime?> CalculateSinceTimestampAsync(TConfig config)
    {
        var latestEntryTimestamp = await FetchLatestEntryTimestampAsync(config);

        return CalculateSinceFromTimestamp(latestEntryTimestamp, "entries");
    }

    /// <summary>
    ///     The treatment family's resume point: the most recent stored treatment (minus the
    ///     catch-up overlap), or <see cref="InitialSyncFloor"/> when none is stored.
    /// </summary>
    protected async Task<DateTime?> CalculateTreatmentSinceTimestampAsync(TConfig config)
    {
        var latestTreatmentTimestamp = await FetchLatestTreatmentTimestampAsync(config);

        return CalculateSinceFromTimestamp(latestTreatmentTimestamp, "treatments");
    }

    /// <summary>
    ///     The device-status family's resume point: the most recent stored device status (minus the
    ///     catch-up overlap), or <c>null</c> — the whole history, whatever
    ///     <see cref="InitialSyncFloor"/> says — when none is stored.
    /// </summary>
    protected async Task<DateTime?> CalculateDeviceStatusCatchUpSinceAsync(TConfig config)
    {
        var latest = await FetchLatestDeviceStatusTimestampAsync(config);
        return TryCalculateCatchUpSince(latest, "device status");
    }

    /// <summary>
    ///     The activity family's resume point, read as
    ///     <see cref="CalculateDeviceStatusCatchUpSinceAsync"/> reads device status.
    /// </summary>
    protected async Task<DateTime?> CalculateActivityCatchUpSinceAsync(TConfig config)
    {
        var latest = await FetchLatestActivityTimestampAsync(config);
        return TryCalculateCatchUpSince(latest, "activity");
    }

    /// <summary>
    ///     One treatment type's resume point: as <see cref="CalculateTreatmentSinceTimestampAsync(TConfig)"/>,
    ///     over the newest stored record of <paramref name="type"/> alone, for a source that fetches
    ///     each treatment type separately and so can leave one behind while its siblings advance.
    /// </summary>
    protected async Task<DateTime?> CalculateTreatmentSinceTimestampAsync(TConfig config, SyncDataType type)
    {
        var latest = await FetchLatestWatermarkAsync(
            () => _publisher!.Treatments.GetLatestTreatmentTimestampAsync(type, ConnectorSource));

        return CalculateSinceFromTimestamp(latest, type.ToString());
    }

    /// <summary>
    ///     The state-span family's resume point: the most recent stored state span outside the
    ///     activity categories (minus the catch-up overlap), or <see cref="InitialSyncFloor"/> when
    ///     none is stored.
    /// </summary>
    protected async Task<DateTime?> CalculateStateSpanSinceTimestampAsync(TConfig config)
    {
        var latest = await FetchLatestWatermarkAsync(
            () => _publisher!.Metadata.GetLatestStateSpanTimestampAsync(ConnectorSource));

        return CalculateSinceFromTimestamp(latest, "state spans");
    }

    /// <summary>
    ///     Reads one resume watermark. A failed read propagates for the reason given in
    ///     <see cref="FetchLatestEntryTimestampAsync"/>.
    /// </summary>
    private async Task<DateTime?> FetchLatestWatermarkAsync(Func<Task<DateTime?>> read)
    {
        if (_publisher is not { IsAvailable: true })
            return null;

        return await read();
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
    ///     caller's cannot narrow the resume point either, since a family whose last crawl failed
    ///     still owes the range below it — an open resume point included, since that is a family
    ///     with nothing stored asking for the source's whole history.
    ///     <para>
    ///     A caller that supplies no bound is not asking for everything: the tenant's own sync
    ///     button sends none, and a family with a resume point still stands on it.
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
    ///     The lower bound for a source that cannot crawl from an open one: as
    ///     <see cref="ResumeFrom(DateTime?, DateTime?)"/>, over a resume point already resolved to
    ///     a concrete timestamp.
    /// </summary>
    protected static DateTime ResumeFrom(DateTime? requested, DateTime resumePoint) =>
        ResumeFrom(requested, (DateTime?)resumePoint) ?? resumePoint;

    /// <summary>
    ///     The lower bound a family other than glucose crawls from on an open-ended request: as
    ///     <see cref="ResumeFrom(DateTime?, DateTime?)"/>, except that a background run's derived
    ///     glucose resume point is no bound at all.
    /// </summary>
    /// <remarks>
    ///     Widening a family to the glucose resume point ties it to glucose succeeding: a glucose
    ///     endpoint that fails every run, or nothing stored for glucose, keeps that point at the
    ///     initial floor, and every other family re-crawls everything above it on every run.
    /// </remarks>
    protected static DateTime? ResumeFrom(SyncRequest request, DateTime? resumePoint) =>
        request.FromIsGlucoseResumePoint ? resumePoint : ResumeFrom(request.From, resumePoint);

    /// <summary>
    ///     The lower bound a whole request crawls from, for a source that cannot crawl from an open
    ///     one. An explicit range is answered as asked: it is the shape a manual re-import of one
    ///     window sends, and widening it back to a resume point below re-crawls everything in
    ///     between. A range naming no lower bound is asking for everything available, so it starts
    ///     at <paramref name="historyFloor"/> — as far back as the connector reaches, which is the
    ///     reading the reset-cursor endpoint documents and the only one under which it resets
    ///     anything.
    /// </summary>
    protected static DateTime ResumeFrom(
        SyncRequest request, DateTime resumePoint, DateTime historyFloor) =>
        request.To is null ? ResumeFrom(request.From, resumePoint) : request.From ?? historyFloor;

    /// <summary>
    ///     Applies the catch-up overlap to a latest-record timestamp: returns the timestamp
    ///     minus a small overlap (to absorb clock drift), or <c>null</c> when there is no
    ///     usable prior timestamp.
    /// </summary>
    private DateTime? TryCalculateCatchUpSince(DateTime? latestTimestamp, string dataType)
    {
        if (latestTimestamp.HasValue && latestTimestamp.Value > DateTime.MinValue.AddMinutes(10))
        {
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
    ///     When no prior data exists, falls back to <see cref="InitialSyncFloor"/>, which may be
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
    protected DateTime DefaultInitialSyncFloor() => Clock.GetUtcNow().UtcDateTime.AddMonths(-6);

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

    /// <summary>
    ///     Publishes one record type's batch through its <see cref="PublishRoutes"/> entry when the
    ///     type is active, reporting progress and recording the outcome through <see cref="RecordPublishOutcome"/>.
    /// </summary>
    /// <param name="dataType">The type to gate and count under when more than one governs the record type.</param>
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
        HashSet<SyncDataType> activeTypes,
        List<T> records,
        TConfig config,
        CancellationToken cancellationToken,
        string? context = null,
        SyncDataType? dataType = null) where T : class
    {
        var route = PublishRoutes.For<T>();
        var type = dataType ?? route.DefaultDataType;
        if (!route.DataTypes.Contains(type))
            throw new ArgumentException(
                $"{typeof(T).Name} is not published under {type}.", nameof(dataType));

        if (!activeTypes.Contains(type)) return false;

        if (records.Count == 0)
        {
            RecordPublishOutcome(result, type, 0, success: true);
            return false;
        }

        await ReportSyncMessageAsync(SyncMessageType.PublishingDataType,
            new() { ["count"] = records.Count.ToString(), ["dataType"] = type.ToString() },
            cancellationToken);

        var success = await PublishByRouteAsync(records, config, cancellationToken);
        RecordPublishOutcome(result, type, records.Count, success, context);
        return success;
    }

    /// <summary>
    ///     Publishes a batch through its record type's route without the per-type bookkeeping of
    ///     <see cref="PublishRecordTypeAsync{T}"/>: no count, no progress message. Every route dispatch
    ///     goes through <see cref="DispatchAsync{T, TOut}"/>.
    /// </summary>
    protected async Task<bool> PublishByRouteAsync<T>(
        IEnumerable<T> records,
        TConfig config,
        CancellationToken cancellationToken) where T : class
    {
        var route = PublishRoutes.For<T>();
        return await DispatchAsync(route, records, config, false,
            (publisher, origin) => route.Publish(publisher, records, ConnectorSource, origin, cancellationToken));
    }

    /// <summary>
    ///     Publishes through a route that hands back its publisher's result, for a caller that needs
    ///     more than whether it landed.
    /// </summary>
    /// <returns>The result, or null when the publisher is unavailable or reported a failure.</returns>
    protected async Task<TResult?> PublishByRouteForResultAsync<T, TResult>(
        IEnumerable<T> records,
        TConfig config,
        CancellationToken cancellationToken) where T : class where TResult : class
    {
        var route = PublishRoutes.ForResult<T, TResult>();
        return await DispatchAsync<T, TResult?>(route, records, config, null,
            (publisher, origin) => route.PublishForResult(publisher, records, ConnectorSource, origin, cancellationToken));
    }

    /// <summary>
    ///     Writes through the record type's "recent" member only what the store does not already hold.
    /// </summary>
    /// <returns>How many records were written, or null when the publisher is unavailable or reported a failure.</returns>
    /// <exception cref="InvalidOperationException">The record type's route has no recent member.</exception>
    protected async Task<int?> PublishRecentByRouteAsync<T>(
        IEnumerable<T> records,
        TConfig config,
        CancellationToken cancellationToken) where T : class
    {
        var route = PublishRoutes.For<T>();
        var publishRecent = route.PublishRecent
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no recent publish member.");
        return await DispatchAsync<T, int?>(route, records, config, null,
            (publisher, origin) => publishRecent(publisher, records, ConnectorSource, origin, cancellationToken));
    }

    /// <summary>
    ///     The one place the publisher is checked for availability, the route's preparation step is run
    ///     and its origin family is resolved.
    /// </summary>
    /// <param name="unavailable">What a caller gets when the publisher is unavailable.</param>
    private async Task<TOut> DispatchAsync<T, TOut>(
        PublishRoute<T> route,
        IEnumerable<T> records,
        TConfig config,
        TOut unavailable,
        Func<IConnectorPublisher, WriteOrigin, Task<TOut>> publish) where T : class
    {
        if (_publisher is not { IsAvailable: true } publisher)
        {
            _logger?.LogWarning("Publisher not available for {RecordType} submission", typeof(T).Name);
            return unavailable;
        }

        if (route.Prepare is { } prepare)
            foreach (var record in records)
                prepare(record, config);

        var origin = route.Origin switch
        {
            PublishOriginFamily.Glucose => await GlucosePublishOriginAsync(),
            PublishOriginFamily.Treatment => await TreatmentPublishOriginAsync(),
            PublishOriginFamily.Device => await DevicePublishOriginAsync(),
            _ => WriteOrigin.Live,
        };
        return await publish(publisher, origin);
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
    ///     accepted. What it withheld as deleted is <see cref="SyncResult.ItemsSkipped"/>.
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

    /// <summary>
    ///     Records an exception that stopped one record type, so the run's other types still sync
    ///     and the run reports failure.
    /// </summary>
    protected void RecordTypeSyncError(SyncResult result, string dataType, Exception ex)
    {
        result.Success = false;
        result.Errors.Add($"Failed to sync {dataType}: {ex.Message}");
        _logger.LogError(ex, "Failed to sync {DataType} for {Connector}", dataType, ConnectorSource);
    }

    /// <summary>
    ///     Records an exception that stopped the run part-way. Caught rather than left to the
    ///     caller, which would also log and report it, because a thrown run loses the counts of the
    ///     types it had already published and the tenant's sync card would show them as unchecked.
    /// </summary>
    protected void RecordSyncError(SyncResult result, Exception ex)
    {
        _logger.LogError(ex, "[{ConnectorSource}] Sync failed", ConnectorSource);
        result.Success = false;
        result.Errors.Add($"Sync error: {ex.Message}");
    }

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
            Timestamp = Clock.GetUtcNow(),
        }, cancellationToken);
    }

    private static SyncPhase PhaseOf(SyncMessageType messageType) => messageType switch
    {
        SyncMessageType.SyncComplete => SyncPhase.Completed,
        SyncMessageType.SyncFailed => SyncPhase.Failed,
        _ => SyncPhase.Syncing,
    };

    protected async Task<bool> PublishInBatchesAsync<T>(
        IEnumerable<T> records,
        TConfig config,
        CancellationToken cancellationToken = default
    ) where T : class
    {
        var array = records.ToArray();
        if (array.Length == 0)
            return true;

        var allSuccessful = true;
        var batchNumber = 1;

        foreach (var batch in array.Chunk(Math.Max(1, config.BatchSize)))
        {
            _logger?.LogDebug(
                "Publishing {RecordType} batch {BatchNumber} with {Count} entries",
                typeof(T).Name,
                batchNumber,
                batch.Length
            );

            if (!await PublishByRouteAsync(batch, config, cancellationToken))
            {
                allSuccessful = false;
                _logger?.LogWarning("Failed to publish {RecordType} batch {BatchNumber}", typeof(T).Name, batchNumber);
            }

            batchNumber++;

            // Small delay between batches to avoid overwhelming the message bus
            await Task.Delay(10, cancellationToken);
        }

        return allSuccessful;
    }

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
        _backfillDeadline = Clock.GetUtcNow() + BackfillBudget;
        try
        {
            if (!await AuthenticateAsync())
            {
                _logger.LogError("Authentication failed for {ConnectorSource}", ConnectorSource);
                return AuthenticationFailedResult();
            }

            var request = CreateBackgroundSyncRequest(since ?? await CalculateSinceTimestampAsync(config));
            request.FromIsGlucoseResumePoint = since is null;

            var result = await PerformSyncInternalAsync(request, config, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Background sync completed successfully for {ConnectorSource}",
                    ConnectorSource
                );

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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "Unexpected error in background SyncDataAsync for {ConnectorSource}",
                ConnectorSource
            );
            return new SyncResult
            {
                Success = false,
                Errors = { ex.Message }
            };
        }
        finally
        {
            _backfillDeadline = null;
        }
    }

    protected virtual SyncRequest CreateBackgroundSyncRequest(DateTime? since) => new()
    {
        From = since,
        DataTypes = SupportedDataTypes,
    };

    protected virtual void Dispose(bool disposing)
    {
        // HttpClient is managed by IHttpClientFactory - do not dispose
    }

    private string? _authenticationFailureReason;

    /// <summary>
    ///     Records a failed authentication along with what the tenant has to fix, for
    ///     <see cref="AuthenticationFailedResult"/> to report in place of the generic wording.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="TrackFailedRequest"/>: most reasons recorded there are written
    ///     for the log — "HTTP Unauthorized", "JSON parsing error". Those must not become what a
    ///     tenant is told to go and do, so only a reason passed here is user-facing.
    /// </remarks>
    protected void TrackFailedAuthentication(string reason)
    {
        _authenticationFailureReason = reason;
        TrackFailedRequest(reason);
    }

    protected void TrackFailedRequest(string? reason = null)
    {
        _logger.LogWarning(
            "[{ConnectorSource}] Request failed{Reason}",
            ConnectorSource,
            reason != null ? $": {reason}" : ""
        );
    }

    /// <summary>
    ///     Clears a stale <see cref="TrackFailedAuthentication"/> reason so a later refusal in the
    ///     same run does not report a failure the source has since accepted.
    /// </summary>
    protected void TrackSuccessfulRequest()
    {
        _authenticationFailureReason = null;
    }

    /// <summary>
    ///     Executes an async operation under the shared connector retry loop, tracking success and
    ///     failure. See <see cref="ConnectorRetryLoop.RunAsync{T}"/> for the attempt-budget and
    ///     delay contract.
    /// </summary>
    /// <param name="reAuthenticateOnUnauthorized">Called on a 401; <c>true</c> retries at once.</param>
    /// <param name="maxRetries">Total attempts, not retries on top of a first try; clamped to a floor of one.</param>
    /// <returns><c>default</c> when the operation failed.</returns>
    protected async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<T?>> operation,
        Func<Task<bool>>? reAuthenticateOnUnauthorized = null,
        int maxRetries = 3,
        string? operationName = null,
        CancellationToken cancellationToken = default
    )
    {
        var opName = operationName ?? "operation";
        Exception? lastException = null;

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
                catch (OperationCanceledException ex) when (ConnectorFaults.IsTimeout(ex, cancellationToken))
                {
                    lastException = ex;
                    _logger.LogWarning(
                        "[{ConnectorSource}] {Operation} timed out (attempt {Attempt})",
                        ConnectorSource,
                        opName,
                        attempt + 1
                    );

                    return RetryStep<T>.RetryAfterDelay;
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
            RetryDelayStrategy,
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

    protected async Task<T?> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default
    )
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content, JsonDefaults.CaseInsensitive);
    }

}
