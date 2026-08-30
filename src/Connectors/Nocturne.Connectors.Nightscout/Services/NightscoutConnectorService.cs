using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services;

public class NightscoutConnectorServiceBase<TConfig> : BaseConnectorService<TConfig>
    where TConfig : NightscoutConnectorConfiguration
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;

    // Starts as the startup defaults (from IConnectorRegistration); replaced with the
    // per-tenant config when AuthenticateWithConfigAsync runs at the start of a sync.
    // Per-instance, no concurrency: connectors are resolved into a fresh DI scope per
    // tenant sync, and SyncDataAsync is not invoked concurrently on the same instance.
    private TConfig _currentConfig;
    private string? _apiSecretHash;
    private string? _resolvedBaseUrl;

    public NightscoutConnectorServiceBase(
        HttpClient httpClient,
        IConnectorServerResolver<TConfig> serverResolver,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        IConnectorRegistration<TConfig> registration,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _currentConfig = registration?.Defaults ?? throw new ArgumentNullException(nameof(registration));
    }

    protected override string ConnectorSource => DataSources.NightscoutConnector;
    public override string ServiceName => "Nightscout";

    // A Nightscout instance is a full data export, so the initial sync (no prior data) imports the
    // source's entire history rather than the default bounded window — capping the first backfill
    // would silently drop older records. Catch-up syncs still resume from each type's own cursor.
    protected override DateTime? InitialSyncFloor => null;


    public override async Task<bool> AuthenticateAsync()
    {
        // Legacy no-config overload; uses whatever config the service was last primed
        // with (startup defaults until AuthenticateWithConfigAsync replaces it).
        // Per-tenant sync uses AuthenticateWithConfigAsync instead.
        return await AuthenticateWithConfigAsync(_currentConfig);
    }

    private async Task<bool> AuthenticateWithConfigAsync(TConfig config)
    {
        _currentConfig = config;
        _resolvedBaseUrl = ResolveBaseUrl(config.Url);

        if (string.IsNullOrEmpty(config.ApiSecret))
        {
            _logger.LogError(
                "[{ConnectorSource}] API secret is not configured",
                ConnectorSource);
            TrackFailedRequest("API secret is not configured");
            return false;
        }

        _apiSecretHash = ComputeApiSecretHash(config.ApiSecret);

        _logger.LogDebug(
            "[{ConnectorSource}] Authenticating with Nightscout at {Url}",
            ConnectorSource,
            _resolvedBaseUrl);

        try
        {
            var headers = GetAuthHeaders();
            var response = await GetWithHeadersAsync(
                $"{_resolvedBaseUrl}/api/v1/entries.json?count=1", headers);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();

                // Detect Cloudflare/WAF challenge pages that block server-to-server requests
                if (IsWafChallengePage(response, body))
                {
                    _logger.LogError(
                        "[{ConnectorSource}] Nightscout instance at {Url} is behind a WAF (e.g. Cloudflare) that is blocking API requests",
                        ConnectorSource,
                        _resolvedBaseUrl);
                    TrackFailedRequest(
                        "Your Nightscout instance is behind a firewall (e.g. Cloudflare) that is blocking Nocturne from syncing. " +
                        "Please add a WAF bypass rule for API paths (e.g. /api/*) or allowlist the Nocturne server IP.");
                    return false;
                }

                _logger.LogError(
                    "[{ConnectorSource}] Nightscout auth check returned HTTP {StatusCode}: {Body}",
                    ConnectorSource,
                    (int)response.StatusCode,
                    body);
                TrackFailedRequest($"Nightscout auth check failed: HTTP {(int)response.StatusCode}");
                return false;
            }

            TrackSuccessfulRequest();
            _logger.LogInformation(
                "[{ConnectorSource}] Successfully authenticated with Nightscout instance",
                ConnectorSource);
            return true;
        }
        catch (Exception ex)
        {
            TrackFailedRequest($"Nightscout authentication failed: {ex.Message}");
            _logger.LogError(ex,
                "[{ConnectorSource}] Failed to connect to Nightscout instance at {Url}",
                ConnectorSource,
                _resolvedBaseUrl);
            return false;
        }
    }

    public override async Task<SyncResult> SyncDataAsync(
        TConfig config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null)
    {
        // _currentConfig starts as startup defaults (empty URL). Prime it with the
        // tenant config before base calls AuthenticateAsync(), which delegates to
        // AuthenticateWithConfigAsync(_currentConfig).
        _currentConfig = config;
        return await base.SyncDataAsync(config, cancellationToken, since, progressReporter);
    }

    protected override Task<bool> EnsureAuthenticatedAsync(
        TConfig config,
        CancellationToken cancellationToken) => AuthenticateWithConfigAsync(config);

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        var activeTypes = ResolveActiveTypes(request, config);

        // On an open-ended catch-up (no explicit upper bound) each data type below resolves its
        // bound through ResumeFrom, from request.From and its own resume point. Explicit ranged
        // syncs (request.To set, e.g. a manual re-import) honour request.From/To as-is.
        var openEnded = request.To is null;

        // Glucose keeps request.From — for background syncs the framework already derived
        // it from the latest glucose entry, so it is glucose's own independent cursor.
        //
        // Each data type below streams fetch-page → publish-page rather than accumulating
        // the whole range first: a multi-year backfill of a high-volume collection held in
        // one list has taken the process out with OutOfMemory, failing unrelated tenants'
        // publishes with it. Pages arrive newest first, so anything a broken crawl never
        // stored sits BELOW the newest stored record where an ordinary catch-up never
        // returns; CrawlAndPublishAsync persists a low-water mark as pages land and resumes
        // below it on the next sync, so a crawl killed by a restart or a failing store
        // self-heals instead of stranding the older history.
        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            try
            {
                var outcome = await CrawlAndPublishAsync(
                    "Glucose", request.From, request.To,
                    FetchGlucosePagesAsync,
                    oldestOf: OldestEntryTime,
                    publishAsync: p => PublishGlucoseDataInBatchesAsync(p, config, cancellationToken));

                RecordPublishOutcome(result, SyncDataType.Glucose, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Glucose: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Glucose for {Connector}", ConnectorSource);
            }
        }

        // Nightscout fetches every treatment type as one batch, so each active sub-type
        // carries the whole batch's outcome.
        SyncDataType[] treatmentTypes =
        [
            SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.ManualBG,
            SyncDataType.BolusCalculations, SyncDataType.Notes, SyncDataType.DeviceEvents
        ];
        if (activeTypes.Any(t => treatmentTypes.Contains(t)))
        {
            try
            {
                // The treatment cursor resolves to a bound rather than to an absent resume point:
                // with none stored it is this connector's own open InitialSyncFloor.
                var treatmentFrom = openEnded
                    ? ResumeFrom(request.From, await CalculateTreatmentSinceTimestampAsync(config))
                    : request.From;

                var outcome = await CrawlAndPublishAsync(
                    "Treatments", treatmentFrom, request.To,
                    FetchTreatmentPagesAsync,
                    oldestOf: p => OldestCreatedAt(p, t => t.CreatedAt),
                    publishAsync: p => PublishTreatmentDataInBatchesAsync(p, config, cancellationToken));

                foreach (var treatmentType in treatmentTypes.Where(activeTypes.Contains))
                    RecordPublishOutcome(result, treatmentType, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Treatments: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Treatments for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.Profiles))
        {
            try
            {
                var profiles = await FetchProfilesAsync();
                await PublishRecordTypeAsync(result, SyncDataType.Profiles, activeTypes,
                    profiles.ToList(), PublishProfileDataAsync, config, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Profiles: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Profiles for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.DeviceStatus))
        {
            try
            {
                var deviceStatusFrom = openEnded
                    ? ResumeFrom(request.From, await CalculateDeviceStatusCatchUpSinceAsync(config) ?? request.From)
                    : request.From;

                var outcome = await CrawlAndPublishAsync(
                    "DeviceStatus", deviceStatusFrom, request.To,
                    FetchDeviceStatusPagesAsync,
                    oldestOf: p => OldestCreatedAt(p, d => d.CreatedAt),
                    publishAsync: p => PublishDeviceStatusAsync(p, config, cancellationToken));

                RecordPublishOutcome(result, SyncDataType.DeviceStatus, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync DeviceStatus: {ex.Message}");
                _logger.LogError(ex, "Failed to sync DeviceStatus for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.Food))
        {
            try
            {
                var foods = await FetchFoodAsync();
                await PublishRecordTypeAsync(result, SyncDataType.Food, activeTypes,
                    foods.ToList(), PublishFoodDataAsync, config, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Food: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Food for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.Activity))
        {
            try
            {
                var activityFrom = openEnded
                    ? ResumeFrom(request.From, await CalculateActivityCatchUpSinceAsync(config) ?? request.From)
                    : request.From;

                var outcome = await CrawlAndPublishAsync(
                    "Activity", activityFrom, request.To,
                    FetchActivityPagesAsync,
                    oldestOf: p => OldestCreatedAt(p, a => a.CreatedAt),
                    publishAsync: p => PublishActivityDataAsync(p, config, cancellationToken));

                RecordPublishOutcome(result, SyncDataType.Activity, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Activity: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Activity for {Connector}", ConnectorSource);
            }
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    ///     Upper bound for the first page of a paginated fetch. Nightscout applies an
    ///     implicit recency window (roughly the last four days) to any query carrying no
    ///     date filter at all, so a fully unbounded first page silently truncates a
    ///     full-history backfill — the short page then reads as end-of-history to the
    ///     pagination loop. Anchoring the bound to "now" keeps every request explicitly
    ///     dated; requests that already carry a bound pass through unchanged.
    /// </summary>
    private static DateTime? AnchorUnboundedFetch(DateTime? from, DateTime? to) =>
        from is null && to is null ? DateTime.UtcNow : to;

    /// <summary>Outcome of one crawled collection, spanning every page of the crawl.</summary>
    private sealed record PagedCrawlOutcome(int Count, bool Success);

    private Task<DateTime?> GetBackfillLowWaterMarkAsync(string collection) =>
        Publisher is { IsAvailable: true } p
            ? p.Metadata.GetBackfillLowWaterMarkAsync(ConnectorSource, collection)
            : Task.FromResult<DateTime?>(null);

    private Task SetBackfillLowWaterMarkAsync(string collection, DateTime? mark) =>
        Publisher is { IsAvailable: true } p
            ? p.Metadata.SetBackfillLowWaterMarkAsync(ConnectorSource, collection, mark)
            : Task.CompletedTask;

    /// <summary>
    ///     Crawls a collection newest-first, publishing each page as it lands, then — when an
    ///     earlier crawl of the collection left a persisted low-water mark — resumes that
    ///     incomplete backfill below the mark. Pages descend from "now", so anything a killed
    ///     crawl never reached sits BELOW the newest stored record and an ordinary catch-up
    ///     never returns for it; the mark is what carries "history below X is still missing"
    ///     across process restarts and store failures.
    /// </summary>
    /// <remarks>
    ///     The resume crawl is deliberately UNBOUNDED below the mark, and that is load-bearing:
    ///     the mark's value only decides where re-crawling starts, never where it stops, so a
    ///     raised or stale mark can only cost redundant (idempotent) re-fetching — every
    ///     missing region below any surviving mark is eventually reached. Bounding the resume
    ///     (e.g. stopping at a previous mark, or persisting gap floors) would turn those same
    ///     states into data loss. The known cost: a failed bounded catch-up leaves a near-now
    ///     mark whose resume re-crawls the full history for a minutes-wide gap — rare, safe,
    ///     and preferred over a more fragile gap bookkeeping.
    /// </remarks>
    private async Task<PagedCrawlOutcome> CrawlAndPublishAsync<T>(
        string collection,
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T[], DateTime?> oldestOf,
        Func<T[], Task<bool>> publishAsync)
    {
        var mark = await GetBackfillLowWaterMarkAsync(collection);

        var primary = await CrawlRangeAsync(
            collection, from, to, pages, oldestOf, publishAsync, fullCrawl: from is null);

        // Resume the incomplete backfill only when this cycle's primary crawl stored cleanly —
        // a store that is failing right now shouldn't be hammered with the deep history too.
        // A full primary crawl (open lower bound) already covers everything below the mark.
        if (mark is null || !primary.Success || from is null)
            return primary;

        var resume = await CrawlRangeAsync(
            collection, null, mark.Value.AddMilliseconds(-1),
            pages, oldestOf, publishAsync, fullCrawl: true);

        return new PagedCrawlOutcome(primary.Count + resume.Count, resume.Success);
    }

    /// <summary>
    ///     One newest-first crawl over a range. A page publish failure stops the crawl (pages
    ///     below a gap would strand it above the resume point) and records the low-water mark
    ///     so the next sync resumes there. Full crawls (open lower bound) also advance the mark
    ///     after every published page — crash protection for multi-hour histories — and clear
    ///     it on reaching the source's beginning; bounded catch-up crawls only ever raise it.
    /// </summary>
    private async Task<PagedCrawlOutcome> CrawlRangeAsync<T>(
        string collection,
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T[], DateTime?> oldestOf,
        Func<T[], Task<bool>> publishAsync,
        bool fullCrawl)
    {
        var count = 0;
        DateTime? lowestPublished = null;
        var success = true;

        try
        {
            await foreach (var page in pages(from, to))
            {
                count += page.Length;

                if (!await publishAsync(page))
                {
                    success = false;
                    break;
                }

                var pageOldest = oldestOf(page);
                if (pageOldest.HasValue)
                    lowestPublished = pageOldest;

                if (fullCrawl && lowestPublished.HasValue)
                    await SetBackfillLowWaterMarkAsync(collection, lowestPublished);
            }
        }
        catch
        {
            // A fetch failure mid-crawl leaves the same gap a publish failure does: record the
            // resume point for what already published before surfacing the error.
            await RaiseBackfillLowWaterMarkAsync(collection, lowestPublished);
            throw;
        }

        if (success && fullCrawl)
        {
            // Reached the source's beginning: the backfill is complete.
            await SetBackfillLowWaterMarkAsync(collection, null);
        }
        else if (!success)
        {
            await RaiseBackfillLowWaterMarkAsync(collection, lowestPublished);
        }

        return new PagedCrawlOutcome(count, success);
    }

    /// <summary>
    ///     Raises the collection's low-water mark to <paramref name="candidate"/> — never lowers
    ///     it: a deeper mark from an earlier failure still describes missing history further down.
    /// </summary>
    private async Task RaiseBackfillLowWaterMarkAsync(string collection, DateTime? candidate)
    {
        if (!candidate.HasValue)
            return;

        var existing = await GetBackfillLowWaterMarkAsync(collection);
        if (existing is null || existing < candidate)
            await SetBackfillLowWaterMarkAsync(collection, candidate);
    }

    /// <summary>
    ///     Streams a paginated Nightscout collection newest-first, one page per iteration,
    ///     so callers never hold more than a page of a multi-year history in memory. Each
    ///     full page steps the upper bound just below its oldest record; a short page is
    ///     the end of the range.
    /// </summary>
    /// <param name="from">Optional inclusive lower bound.</param>
    /// <param name="to">Optional inclusive upper bound; anchored to now when both bounds are open.</param>
    /// <param name="buildUrl">Builds the request URL for the given bounds.</param>
    /// <param name="oldestOf">Extracts the oldest record time from a page, or null when the page has no usable times.</param>
    /// <param name="operationName">Operation label for fetch logging.</param>
    /// <param name="keep">Optional page filter; pagination still steps on the unfiltered page.</param>
    private async IAsyncEnumerable<T[]> FetchPagesAsync<T>(
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, string> buildUrl,
        Func<T[], DateTime?> oldestOf,
        string operationName,
        Func<T[], T[]>? keep = null)
    {
        var currentTo = AnchorUnboundedFetch(from, to);

        while (true)
        {
            var page = await FetchDataAsync<T[]>(buildUrl(from, currentTo), operationName);

            // FetchDataAsync reports failure (retries exhausted, non-retryable HTTP, bad JSON) as
            // null rather than throwing; <see cref="BaseConnectorService{TConfig}.FetchFailed"/> is
            // why that is not the end of the range.
            if (page == null)
                throw FetchFailed(operationName);

            if (page.Length == 0)
                yield break;

            var kept = keep is null ? page : keep(page);
            if (kept.Length > 0)
                yield return kept;

            // Fewer than MaxCount means we've fetched everything in this range
            if (page.Length < _currentConfig.MaxCount)
                yield break;

            var oldestDate = oldestOf(page);
            if (!oldestDate.HasValue)
                yield break;

            // Avoid an infinite loop if the oldest date hasn't moved
            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value)
                yield break;

            // Next page: records older than the oldest we've seen
            currentTo = oldestDate.Value.AddMilliseconds(-1);

            if (from.HasValue && currentTo < from)
                yield break;

            _logger.LogDebug(
                "[{ConnectorSource}] Paginating {Operation}, next page before {Before:yyyy-MM-dd HH:mm:ss}",
                ConnectorSource,
                operationName,
                currentTo);
        }
    }

    private static DateTime? OldestEntryTime(Entry[] page)
    {
        var oldestMs = page.Min(e => e.Mills);
        return oldestMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(oldestMs).UtcDateTime
            : null;
    }

    private static DateTimeOffset? ParseCreatedAt(string? createdAt) =>
        DateTimeOffset.TryParse(createdAt, out var parsed) ? parsed : null;

    /// <summary>
    ///     Oldest created_at on a page. Uses DateTimeOffset for consistent UTC comparison
    ///     regardless of system timezone.
    /// </summary>
    private static DateTime? OldestCreatedAt<T>(T[] page, Func<T, string?> createdAtOf) =>
        page.Select(item => ParseCreatedAt(createdAtOf(item))?.UtcDateTime).Min();

    /// <summary>
    ///     Oldest created_at on a page as the source wrote it — the key the source orders and
    ///     filters by. Labelled UTC so formatting it back into a bound reproduces that wall clock
    ///     verbatim rather than shifting it by the host's timezone.
    /// </summary>
    private static DateTime? OldestWrittenCreatedAt<T>(T[] page, Func<T, string?> createdAtOf) =>
        page.Select(item => ParseCreatedAt(createdAtOf(item)) is { } parsed
                ? DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Utc)
                : (DateTime?)null)
            .Min();

    /// <summary>
    ///     Whether a created_at falls inside the caller's window. A value that will not parse is
    ///     kept: the crawl has never dropped records it cannot date.
    /// </summary>
    private static bool WithinWindow(string? createdAt, DateTime? from, DateTime? to)
    {
        if (ParseCreatedAt(createdAt) is not { } parsed)
            return true;

        return (from is null || parsed.UtcDateTime >= from.Value)
            && (to is null || parsed.UtcDateTime <= to.Value);
    }

    // Real-world UTC offsets span -12:00 to +14:00.
    private static readonly TimeSpan MaxUtcOffset = TimeSpan.FromHours(14);

    /// <summary>
    ///     Pages a created_at collection. Legacy Nightscout stores created_at as a string and
    ///     compares it as one, so a record an old uploader wrote with a local offset
    ///     ("2020-06-15T20:00:00+10:00") orders by its wall clock rather than its instant — up to
    ///     <see cref="MaxUtcOffset"/> away. The requested window is widened by that envelope so such
    ///     records are returned at all, and each page is filtered back to the true window here.
    ///     Only the opening bounds are widened: the page cursor is already a wall clock the source
    ///     returned, so widening it again would step over records the source has yet to serve.
    ///     The filter's ceiling is the anchor the fetch bound was widened from, so an unbounded
    ///     backfill still stops at "now" rather than importing a future-dated device clock.
    /// </summary>
    private IAsyncEnumerable<T[]> FetchCreatedAtPagesAsync<T>(
        DateTime? from,
        DateTime? to,
        string collection,
        Func<T, string?> createdAtOf,
        string operationName)
    {
        var anchoredTo = AnchorUnboundedFetch(from, to);

        return FetchPagesAsync<T>(
            from - MaxUtcOffset,
            anchoredTo + MaxUtcOffset,
            (pageFrom, pageTo) => BuildCreatedAtUrl(collection, pageFrom, pageTo),
            page => OldestWrittenCreatedAt(page, createdAtOf),
            operationName,
            page => page.Where(item => WithinWindow(createdAtOf(item), from, anchoredTo)).ToArray());
    }

    private async IAsyncEnumerable<Entry[]> FetchGlucosePagesAsync(DateTime? from, DateTime? to)
    {
        await foreach (var page in FetchPagesAsync<Entry>(
            from, to, BuildEntriesUrl, OldestEntryTime, "FetchGlucosePages"))
        {
            foreach (var entry in page)
                entry.DataSource = ConnectorSource;
            yield return page;
        }
    }

    private async IAsyncEnumerable<Treatment[]> FetchTreatmentPagesAsync(DateTime? from, DateTime? to)
    {
        await foreach (var page in FetchCreatedAtPagesAsync<Treatment>(
            from, to, "treatments", t => t.CreatedAt, "FetchTreatments"))
        {
            foreach (var treatment in page)
                treatment.DataSource = ConnectorSource;
            yield return page;
        }
    }

    protected override async Task<IEnumerable<Profile>> FetchProfilesAsync()
    {
        var profiles = await FetchDataAsync<Profile[]>(
            "/api/v1/profile.json",
            "FetchProfiles");

        if (profiles == null || profiles.Length == 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] No profiles found on Nightscout instance",
                ConnectorSource);
            return [];
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} profiles from Nightscout",
            ConnectorSource,
            profiles.Length);

        return profiles;
    }

    private IAsyncEnumerable<DeviceStatus[]> FetchDeviceStatusPagesAsync(DateTime? from, DateTime? to) =>
        FetchCreatedAtPagesAsync<DeviceStatus>(
            from, to, "devicestatus", d => d.CreatedAt, "FetchDeviceStatus");

    private async Task<IEnumerable<Food>> FetchFoodAsync()
    {
        var foods = await FetchDataAsync<Food[]>(
            $"/api/v1/food.json?count={_currentConfig.MaxCount}",
            "FetchFood");

        if (foods == null || foods.Length == 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] No food records found on Nightscout instance",
                ConnectorSource);
            return [];
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} food records from Nightscout",
            ConnectorSource,
            foods.Length);

        return foods;
    }

    private IAsyncEnumerable<Activity[]> FetchActivityPagesAsync(DateTime? from, DateTime? to) =>
        FetchCreatedAtPagesAsync<Activity>(
            from, to, "activity", a => a.CreatedAt, "FetchActivity");

    private async Task<T?> FetchDataAsync<T>(string url, string operationName) where T : class
    {
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(url),
            _retryDelayStrategy,
            maxRetries: _currentConfig.MaxRetryAttempts,
            operationName: operationName);
    }

    private async Task<T?> FetchDataCoreAsync<T>(string url) where T : class
    {
        var headers = GetAuthHeaders();
        var absoluteUrl = _resolvedBaseUrl != null ? $"{_resolvedBaseUrl}{url}" : url;
        var response = await GetWithHeadersAsync(absoluteUrl, headers);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode);
        }

        return await DeserializeResponseAsync<T>(response);
    }

    private string BuildEntriesUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/entries.json?count={_currentConfig.MaxCount}";

        if (from.HasValue)
        {
            var fromMs = new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            url += $"&find[date][$gte]={fromMs}";
        }

        if (to.HasValue)
        {
            var toMs = new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            url += $"&find[date][$lte]={toMs}";
        }

        return url;
    }

    private string BuildCreatedAtUrl(string collection, DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/{collection}.json?count={_currentConfig.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    /// <summary>
    /// Normalises a tenant-configured Nightscout URL: supplies <c>https://</c> when the tenant
    /// stored a bare host, and trims any trailing slash so callers can append paths directly.
    /// </summary>
    /// <param name="configUrl">The URL as stored in the tenant's connector configuration.</param>
    /// <exception cref="InvalidOperationException">The configured URL is null or empty.</exception>
    public static string ResolveBaseUrl(string configUrl)
    {
        if (string.IsNullOrEmpty(configUrl))
            throw new InvalidOperationException("Nightscout URL is not configured");

        var url = configUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? configUrl
            : $"https://{configUrl}";

        return url.TrimEnd('/');
    }

    private Dictionary<string, string> GetAuthHeaders()
    {
        return new Dictionary<string, string>
        {
            ["api-secret"] = _apiSecretHash ?? ComputeApiSecretHash(_currentConfig.ApiSecret)
        };
    }

    internal static string ComputeApiSecretHash(string apiSecret)
    {
        if (IsAlreadySha1Hash(apiSecret))
            return apiSecret.ToLowerInvariant();

        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool IsAlreadySha1Hash(string value)
    {
        return value.Length == 40 && value.All(c => char.IsAsciiHexDigit(c));
    }

    /// <summary>
    ///     Detects WAF challenge pages (Cloudflare, Akamai, etc.) that block server-to-server API requests.
    ///     These return HTML instead of JSON and typically include challenge scripts.
    /// </summary>
    private static bool IsWafChallengePage(HttpResponseMessage response, string body)
    {
        // Check for Cloudflare server header
        if (response.Headers.TryGetValues("server", out var serverValues) &&
            serverValues.Any(v => v.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)))
        {
            // Cloudflare returning non-JSON (challenge page) for an API request
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check for cf-ray header (Cloudflare) with HTML body containing challenge markers
        if (response.Headers.Contains("cf-ray") &&
            body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

/// <summary>
/// Nightscout connector service for syncing data from a Nightscout instance.
/// </summary>
public class NightscoutConnectorService : NightscoutConnectorServiceBase<NightscoutConnectorConfiguration>
{
    public NightscoutConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<NightscoutConnectorConfiguration> serverResolver,
        ILogger<NightscoutConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        IConnectorRegistration<NightscoutConnectorConfiguration> registration,
        IConnectorPublisher? publisher = null
    ) : base(httpClient, serverResolver, logger, retryDelayStrategy, rateLimitingStrategy, registration, publisher) { }
}
