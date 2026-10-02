using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;

namespace Nocturne.Connectors.GoogleHealth.Services;

public sealed class GoogleHealthConnectorService(
    HttpClient httpClient,
    IConnectorServerResolver<GoogleHealthConnectorConfiguration> serverResolver,
    GoogleHealthClient google,
    GoogleHealthAuthTokenProvider oauth,
    IGoogleHealthReadingWriter writer,
    IGoogleHealthSyncCoordinator coordinator,
    IConnectorConfigurationService connectorConfigurations,
    IConnectorSyncCursorStore cursorStore,
    ITenantAccessor tenantAccessor,
    IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration> configurationLoader,
    ILogger<GoogleHealthConnectorService> logger,
    IConnectorPublisher? publisher = null)
    : BaseConnectorService<GoogleHealthConnectorConfiguration>(httpClient, serverResolver, logger, publisher)
{
    private const string ConnectorName = "GoogleHealth";

    protected override string ConnectorSource => DataSources.GoogleHealthConnector;
    public override string ServiceName => ServiceNames.GoogleHealthConnector;
    protected override DateTime? InitialSyncFloor => null;

    // The base watermark (CalculateSinceTimestampAsync) only tracks the glucose/treatment
    // families via IConnectorPublisher, which Google Health never publishes through — so its
    // own resume point is persisted here instead of relying on (and bypassing) the base one.
    private const string LastSyncedToKey = "lastSyncedTo";
    public const string RuntimeStateResource = "health";

    public override Task<SyncResult> SyncDataAsync(
        GoogleHealthConnectorConfiguration config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null) =>
        // The managed backfill/live window resolved inside PerformSyncInternalAsync now owns
        // Google Health's resume point entirely; the value passed through here is never read, it
        // only needs to be non-null so the base class skips its own (glucose/treatment-only) watermark.
        base.SyncDataAsync(config, cancellationToken, since ?? DateTime.UtcNow, progressReporter);

    // Matches GoogleHealthConnectorConfiguration's own validation floor for an explicit ImportFrom.
    private static readonly DateTimeOffset EarliestSupportedDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string BackfillCursorKey = "backfillCursorDate";
    private const string BackfillFloorKey = "backfillFloorDate";
    private const string BackfillCompleteKey = "backfillComplete";
    private const string BackfillChunkDaysKey = "backfillChunkDays";

    /// <summary>
    ///     Persisted backfill progress: <see cref="CursorDate"/> is the oldest UTC day already
    ///     imported (null before the very first sync), <see cref="FloorDate"/> is the oldest day the
    ///     backfill is aiming for.
    /// </summary>
    private readonly record struct BackfillState(
        DateTimeOffset? CursorDate, DateTimeOffset FloorDate, bool Complete, int? ChunkDays);

    private readonly record struct GoogleHealthSyncWindow(DateTimeOffset From, DateTimeOffset To, bool IsBackfillDay, bool IsManaged);

    /// <summary>
    ///     Resolves the next historical calendar-month window. The live window is processed
    ///     separately on every managed sync, before this backfill window. Caller-supplied windows
    ///     are completed in full without touching backfill state; reconciliation splits oversized
    ///     windows as needed. An omitted lower bound uses the earliest supported date.
    /// </summary>
    private async Task<GoogleHealthSyncWindow> ResolveWindowAsync(
        SyncRequest request, GoogleHealthConnectorConfiguration config, DateTimeOffset now, CancellationToken ct)
    {
        var today = new DateTimeOffset(DateTime.SpecifyKind(now.UtcDateTime.Date, DateTimeKind.Utc));
        if (request.To is { } requestedTo)
        {
            var explicitTo = new DateTimeOffset(DateTime.SpecifyKind(requestedTo, DateTimeKind.Utc));
            var explicitFrom = request.From is { } requestedFrom
                ? new DateTimeOffset(DateTime.SpecifyKind(requestedFrom, DateTimeKind.Utc))
                : EarliestSupportedDate;
            return new GoogleHealthSyncWindow(explicitFrom, explicitTo, IsBackfillDay: false, IsManaged: false);
        }

        var state = await LoadBackfillStateAsync(ct);
        if (state.CursorDate is null)
        {
            var floor = ComputeBackfillFloor(config, today);
            state = new BackfillState(today, floor, floor >= today, null);
            await SaveBackfillStateAsync(state, ct);
        }
        if (state.Complete)
            return new GoogleHealthSyncWindow(today, today, IsBackfillDay: false, IsManaged: true);

        var cursor = state.CursorDate!.Value;
        var monthStart = new DateTimeOffset(cursor.Year, cursor.Month, 1, 0, 0, 0, TimeSpan.Zero);
        if (monthStart == cursor) monthStart = monthStart.AddMonths(-1);
        var from = state.ChunkDays is { } chunkDays
            ? cursor.AddDays(-chunkDays)
            : monthStart;
        if (from < monthStart) from = monthStart;
        if (from < state.FloorDate) from = state.FloorDate;
        return new GoogleHealthSyncWindow(from, cursor, IsBackfillDay: true, IsManaged: true);
    }

    /// <summary>
    ///     Advances the persisted backfill state after a successful sync. Returns true only the run
    ///     that first reaches the floor, so the one-time explicit <see cref="GoogleHealthConnectorConfiguration.ImportFrom"/>
    ///     is consumed once the whole requested history has actually landed, not after a single call.
    /// </summary>
    private async Task<bool> AdvanceBackfillStateAsync(
        GoogleHealthSyncWindow window, GoogleHealthConnectorConfiguration config, CancellationToken ct)
    {
        var state = await LoadBackfillStateAsync(ct);
        if (state.CursorDate is null)
        {
            var today = new DateTimeOffset(DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc));
            var floor = ComputeBackfillFloor(config, today);
            var complete = floor >= today;
            await SaveBackfillStateAsync(new BackfillState(today, floor, complete, null), ct);
            return complete;
        }
        if (window.IsBackfillDay)
        {
            var reachedFloor = window.From <= state.FloorDate;
            var monthStart = new DateTimeOffset(window.To.Year, window.To.Month, 1, 0, 0, 0, TimeSpan.Zero);
            if (monthStart == window.To) monthStart = monthStart.AddMonths(-1);
            var completedMonth = window.From <= monthStart;
            await SaveBackfillStateAsync(
                state with
                {
                    CursorDate = window.From,
                    Complete = reachedFloor,
                    ChunkDays = completedMonth ? null : state.ChunkDays
                },
                ct);
            return reachedFloor && !state.Complete;
        }
        return false;
    }

    private async Task<DateTimeOffset> LiveFromAsync(CancellationToken ct)
    {
        var watermark = await LoadWatermarkAsync(ct);
        var from = watermark is { } lastSyncedTo
            ? new DateTimeOffset(DateTime.SpecifyKind(lastSyncedTo, DateTimeKind.Utc)).AddMinutes(-5)
            : new DateTimeOffset(DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc));
        // Minute-based heart-rate IDs must be recomputed from the entire overlapping minute.
        return new DateTimeOffset(from.Ticks - from.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    private static DateTimeOffset ComputeBackfillFloor(GoogleHealthConnectorConfiguration config, DateTimeOffset today) =>
        string.IsNullOrWhiteSpace(config.ImportFrom)
            ? today.AddDays(-config.HistoryDays)
            : new DateTimeOffset(DateTime.SpecifyKind(
                DateTimeOffset.Parse(config.ImportFrom, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime.Date,
                DateTimeKind.Utc));

    private async Task<Dictionary<string, JsonElement>> LoadRuntimeStateAsync(CancellationToken ct)
    {
        var cursor = await cursorStore.GetAsync(ConnectorName, RuntimeStateResource, ct);
        return cursor?.LastGuid is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [] : [];
    }

    private Task SaveRuntimeStateAsync(Dictionary<string, JsonElement> state, CancellationToken ct) =>
        cursorStore.SetAsync(ConnectorName, RuntimeStateResource,
            new(null, JsonSerializer.Serialize(state)), ct);

    private async Task<BackfillState> LoadBackfillStateAsync(CancellationToken ct)
    {
        var configuration = await LoadRuntimeStateAsync(ct);
        return new BackfillState(
            ParseStoredDate(configuration, BackfillCursorKey),
            ParseStoredDate(configuration, BackfillFloorKey) ?? EarliestSupportedDate,
            configuration.TryGetValue(BackfillCompleteKey, out var complete) && complete.ValueKind == JsonValueKind.True,
            configuration.TryGetValue(BackfillChunkDaysKey, out var chunkDays) &&
                chunkDays.ValueKind == JsonValueKind.Number
                ? Math.Max(1, chunkDays.GetInt32())
                : null);
    }

    private static DateTimeOffset? ParseStoredDate(Dictionary<string, JsonElement> configuration, string key) =>
        configuration.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(element.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : null;

    private async Task SaveBackfillStateAsync(BackfillState state, CancellationToken ct)
    {
        var configuration = await LoadRuntimeStateAsync(ct);
        configuration[BackfillCursorKey] = state.CursorDate is { } cursor
            ? JsonSerializer.SerializeToElement(cursor.UtcDateTime.ToString("o"))
            : JsonSerializer.SerializeToElement<string?>(null);
        configuration[BackfillFloorKey] = JsonSerializer.SerializeToElement(state.FloorDate.UtcDateTime.ToString("o"));
        configuration[BackfillCompleteKey] = JsonSerializer.SerializeToElement(state.Complete);
        if (state.ChunkDays is { } chunkDays)
            configuration[BackfillChunkDaysKey] = JsonSerializer.SerializeToElement(chunkDays);
        else
            configuration.Remove(BackfillChunkDaysKey);
        await SaveRuntimeStateAsync(configuration, ct);
    }

    private async Task TryReduceBackfillWindowAsync(GoogleHealthSyncWindow window)
    {
        var days = Math.Max(1, (int)Math.Ceiling((window.To - window.From).TotalDays / 2));
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var state = await LoadBackfillStateAsync(cleanup.Token);
            await SaveBackfillStateAsync(state with { ChunkDays = days }, cleanup.Token);
            logger.LogWarning(
                "Google Health historical window {From} to {To} did not complete; retrying with at most {ChunkDays} day(s)",
                window.From, window.To, days);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not persist a smaller Google Health retry window for {From} to {To}",
                window.From, window.To);
        }
    }

    private async Task<DateTime?> LoadWatermarkAsync(CancellationToken ct) =>
        ParseStoredDate(await LoadRuntimeStateAsync(ct), LastSyncedToKey)?.UtcDateTime;

    private async Task PersistWatermarkAsync(DateTimeOffset to, CancellationToken ct)
    {
        var configuration = await LoadRuntimeStateAsync(ct);
        // Never move the resume point backwards: a manual/admin resync of an older window must
        // not widen every later periodic sync back into a re-crawl of everything since.
        if (configuration.TryGetValue(LastSyncedToKey, out var existing) &&
            existing.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(existing.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var existingValue) &&
            existingValue >= to.UtcDateTime)
            return;
        configuration[LastSyncedToKey] = JsonSerializer.SerializeToElement(to.UtcDateTime.ToString("o"));
        await SaveRuntimeStateAsync(configuration, ct);
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        GoogleHealthConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult();
        var tenantId = tenantAccessor.TenantId;
        await using var gate = await coordinator.AcquireAsync(tenantId, cancellationToken);
        try
        {
            config = await configurationLoader.LoadForTenantAsync(cancellationToken);
            if (!config.Enabled) return Complete(result);
            var selected = ResolveActiveTypes(request, config)
                .Select(type => GoogleHealthClient.TryGetDataType(type, out var dataType)
                    ? dataType
                    : throw new GoogleHealthException("unsupported_type"))
                .ToArray();
            if (config.PreviewOnly || selected.Length == 0)
            {
                return Complete(result);
            }

            await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.RefreshingSession);
            var session = await SessionAsync(config, cancellationToken);
            var active = selected
                .Where(type => session.Scopes.Contains(GoogleHealthClient.ScopeFor(type), StringComparer.Ordinal))
                .ToArray();
            if (active.Length == 0)
                throw new GoogleHealthException("permission_denied", stage: "scope_validation");
            var missingConsent = selected.Except(active, StringComparer.Ordinal).ToArray();

            var now = DateTimeOffset.UtcNow;
            var window = await ResolveWindowAsync(request, config, now, cancellationToken);
            await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.Reading, completedDataTypes: 0, totalDataTypes: active.Length);

            if (!window.IsManaged)
            {
                logger.LogInformation(
                    "Starting explicitly bounded Google Health sync for tenant {TenantId} from {From} to {To}. Active data types: {ActiveDataTypes}",
                    tenantId, window.From, window.To, string.Join(',', active));
                await ReadWithRefreshAsync(
                    config, session.AccessToken!, active, window.From, window.To, tenantId, result, cancellationToken);
                if (missingConsent.Length == 0)
                    await PersistWatermarkAsync(window.To, cancellationToken);
                return Complete(result, missingConsent.Length == 0
                    ? string.Empty
                    : GoogleHealthErrorCode.Encode("partial_consent", missingConsent));
            }

            var liveFrom = await LiveFromAsync(cancellationToken);
            logger.LogInformation(
                "Starting live Google Health sync for tenant {TenantId} from {From} to {To}. Active data types: {ActiveDataTypes}",
                tenantId, liveFrom, now, string.Join(',', active));
            var accessToken = await ReadWithRefreshAsync(
                config, session.AccessToken!, active, liveFrom, now, tenantId, result, cancellationToken);
            if (missingConsent.Length == 0)
                await PersistWatermarkAsync(now, cancellationToken);

            if (window.IsBackfillDay && window.From < window.To)
            {
                logger.LogInformation(
                    "Starting historical Google Health month for tenant {TenantId} from {From} to {To}. Active data types: {ActiveDataTypes}",
                    tenantId, window.From, window.To, string.Join(',', active));
                try
                {
                    await ReadWithRefreshAsync(
                        config, accessToken, active, window.From, window.To, tenantId, result, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await TryReduceBackfillWindowAsync(window);
                    throw;
                }
                catch (GoogleHealthException ex) when (ex.Message is
                    "history_too_large" or "rate_limited" or "google_unavailable" or "internal_sync_native_write")
                {
                    await TryReduceBackfillWindowAsync(window);
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
                {
                    await TryReduceBackfillWindowAsync(window);
                    throw;
                }
                if (missingConsent.Length == 0)
                {
                    var justCompletedBackfill = await AdvanceBackfillStateAsync(window, config, cancellationToken);
                    if (justCompletedBackfill && !string.IsNullOrWhiteSpace(config.ImportFrom))
                        await ConsumeImportFromAsync(cancellationToken);
                }
            }
            return Complete(result, missingConsent.Length == 0
                ? string.Empty
                : GoogleHealthErrorCode.Encode("partial_consent", missingConsent));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Google Health import was cancelled for tenant {TenantId}", tenantId);
            throw;
        }
        catch (Exception ex) when (ex is GoogleHealthException or HttpRequestException or JsonException or TaskCanceledException)
        {
            var error = ex as GoogleHealthException ?? new GoogleHealthException(
                ex is JsonException ? "invalid_google_response" : "google_unavailable",
                stage: ex is JsonException ? "response_parse" : "network");
            LogFailure(ex, error, tenantId);
            if (error.Message == "reconnect_required")
                await ClearSessionAsync(cancellationToken);
            return Fail(result, GoogleHealthErrorCode.Encode(
                error.Message,
                error.DataType is null ? null : [error.DataType]));
        }
        catch (Exception ex)
        {
            var diagnosticId = Guid.NewGuid().ToString("N")[..12];
            logger.LogError(ex,
                "Unexpected Google Health import failure for tenant {TenantId}; diagnostic {DiagnosticId}. Message: {ExceptionMessage}",
                tenantId, diagnosticId, ex.Message);
            return Fail(result, "internal_sync");
        }
        finally
        {
            await coordinator.CompleteScheduledAsync(tenantId);
        }
    }

    private async Task<GoogleHealthTokenSession> SessionAsync(
        GoogleHealthConnectorConfiguration config,
        CancellationToken ct,
        bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(config.RefreshToken))
            throw new GoogleHealthException("reconnect_required", stage: "session_read");

        var scopes = (config.GrantedScopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await oauth.SeedSessionAsync(new GoogleHealthTokenSession(config.RefreshToken, scopes));
        if (forceRefresh)
            oauth.InvalidateToken();

        var accessToken = await oauth.GetValidTokenAsync(config, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new GoogleHealthException("invalid_token_response", stage: "token_refresh");

        var session = await oauth.GetCurrentSessionAsync() ??
            throw new GoogleHealthException("invalid_token_response", stage: "token_cache");
        await PersistSessionAsync(session, ct);
        return session;
    }

    private async Task<string> ReadWithRefreshAsync(
            GoogleHealthConnectorConfiguration config,
            string accessToken,
            string[] active,
            DateTimeOffset from,
            DateTimeOffset to,
            Guid tenantId,
            SyncResult result,
            CancellationToken ct)
    {
        var previousCounts = result.ItemsSynced.ToDictionary();
        try
        {
            await ReadOnceAsync(config, accessToken, active, from, to, tenantId, result, ct);
            return accessToken;
        }
        catch (GoogleHealthException error) when (error.Message == "history_too_large" &&
            error.Stage?.StartsWith("native_reconciliation", StringComparison.Ordinal) == true &&
            to - from > TimeSpan.FromDays(1))
        {
            var middle = new DateTimeOffset((from + (to - from) / 2).UtcDateTime.Date, TimeSpan.Zero);
            if (middle <= from) middle = new DateTimeOffset(from.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            RestoreCounts();
            var token = await ReadWithRefreshAsync(config, accessToken, active, from, middle, tenantId, result, ct);
            return await ReadWithRefreshAsync(config, token, active, middle, to, tenantId, result, ct);
        }
        catch (GoogleHealthException first) when (first.Message == "access_token_rejected")
        {
            logger.LogInformation(
                "Google Health access token was rejected for tenant {TenantId}; refreshing once",
                tenantId);
            await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.RefreshingSession);
            var refreshed = await SessionAsync(config, ct, forceRefresh: true);
            RestoreCounts();
            try
            {
                await ReadOnceAsync(config, refreshed.AccessToken!, active, from, to, tenantId, result, ct);
                return refreshed.AccessToken!;
            }
            catch (GoogleHealthException second) when (second.Message == "access_token_rejected")
            {
                throw new GoogleHealthException("reconnect_required", stage: second.Stage,
                    dataType: second.DataType, providerReason: second.ProviderReason,
                    providerStatus: second.ProviderStatus);
            }
        }

        void RestoreCounts()
        {
            result.ItemsSynced.Clear();
            foreach (var (type, count) in previousCounts) result.ItemsSynced[type] = count;
        }
    }

    private async Task ReadOnceAsync(
            GoogleHealthConnectorConfiguration config,
            string accessToken,
            string[] active,
            DateTimeOffset from,
            DateTimeOffset to,
            Guid tenantId,
            SyncResult result,
            CancellationToken ct)
    {
        foreach (var type in active)
            result.ItemsSynced.TryAdd(GoogleHealthClient.TryGetSyncDataType(type, out var dataType)
                ? dataType
                : throw new GoogleHealthException("unsupported_type"), 0);
        for (var index = 0; index < active.Length; index++)
        {
            var type = active[index];
            var reconciliationRun = Guid.Empty;
            var stage = "native_reconciliation_begin";
            try
            {
                    reconciliationRun = await writer.BeginReconciliationAsync([type], from, to, ct);
                    stage = "google_read";
                    await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.Reading, type, index, active.Length, 0);
                    async Task PageRead(int pages)
                    {
                        await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.Reading, type, index, active.Length, pages);
                    }
                    if (type == "sleep")
                        await foreach (var page in google.ReadSleepPagesAsync(accessToken, from, to, ct, PageRead))
                        {
                            var unique = page.Where(session => !string.IsNullOrWhiteSpace(session.OriginalId))
                                .DistinctBy(session => session.OriginalId, StringComparer.Ordinal).ToArray();
                            stage = "native_reconciliation_stage";
                            await writer.StageReconciliationIdsAsync(
                                reconciliationRun, type,
                                unique.Select(session => session.OriginalId!).ToArray(), ct);
                            stage = "native_write";
                            await writer.WriteAsync([], unique, config.BatchSize, ct);
                            result.ItemsSynced[SyncDataType.Sleep] =
                                result.ItemsSynced.GetValueOrDefault(SyncDataType.Sleep) + unique.Length;
                        }
                    else if (type == "heart-rate")
                    {
                        // Per-page staging enforces the reconciliation cap before the minute
                        // accumulator grows, including when live catch-up spans years.
                        var buckets = new Dictionary<long, HeartRateBucket>();
                        await foreach (var page in google.ReadPagesAsync(accessToken, type, from, to, ct, PageRead))
                        {
                            var pageBuckets = new Dictionary<long, HeartRateBucket>();
                            AddHeartRateBuckets(pageBuckets, page);
                            stage = "native_reconciliation_stage";
                            await writer.StageReconciliationIdsAsync(
                                reconciliationRun, type,
                                MaterializeHeartRateBuckets(pageBuckets).Select(GoogleHealthClient.Key).ToArray(), ct);
                            AddHeartRateBuckets(buckets, page);
                        }
                        var unique = MaterializeHeartRateBuckets(buckets);
                        stage = "native_write";
                        await writer.WriteAsync(unique, [], config.BatchSize, ct);
                        AddCount(result, type, unique.Count);
                    }
                    else
                        await foreach (var page in google.ReadPagesAsync(accessToken, type, from, to, ct, PageRead))
                        {
                            var unique = page.DistinctBy(GoogleHealthClient.Key, StringComparer.Ordinal).ToArray();
                            stage = "native_reconciliation_stage";
                            await writer.StageReconciliationIdsAsync(
                                reconciliationRun, type,
                                unique.Select(GoogleHealthClient.Key).ToArray(), ct);
                            stage = "native_write";
                            await writer.WriteAsync(unique, [], config.BatchSize, ct);
                            AddCount(result, type, unique.Length);
                        }
                    await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.Integrating, type, index, active.Length);
                    stage = "native_reconciliation_complete";
                    await writer.CompleteReconciliationAsync(reconciliationRun, ct);
                    await coordinator.ReportAsync(tenantId, GoogleHealthSyncPhase.Reading, type, index + 1, active.Length);
            }
            catch (Exception ex)
            {
                if (reconciliationRun != Guid.Empty)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await writer.AbandonReconciliationAsync(reconciliationRun, cleanup.Token);
                    }
                    catch (Exception cleanupException)
                    {
                        logger.LogWarning(cleanupException, "Could not discard Google Health reconciliation run {RunId}", reconciliationRun);
                    }
                }

                // Provider failures already carry their safe public code. Anything else is a
                // local failure (usually PostgreSQL/reconciliation) and must retain its stage and
                // data type instead of being flattened to an unhelpful generic internal_sync.
                if (ex is GoogleHealthException or OperationCanceledException)
                    throw;
                var code = stage.StartsWith("native_", StringComparison.Ordinal)
                    ? "internal_sync_native_write"
                    : "internal_sync";
                throw new GoogleHealthException(
                    code,
                    stage: stage,
                    dataType: type,
                    providerReason: ex.GetType().Name,
                    innerException: ex);
            }
        }
    }

    private async Task PersistSessionAsync(GoogleHealthTokenSession session, CancellationToken ct)
    {
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        var scopes = string.Join(' ', session.Scopes.Distinct(StringComparer.Ordinal));
        if (secrets.GetValueOrDefault("refreshToken") == session.RefreshToken &&
            secrets.GetValueOrDefault("grantedScopes") == scopes)
            return;
        secrets["refreshToken"] = session.RefreshToken;
        secrets["grantedScopes"] = scopes;
        await connectorConfigurations.SaveSecretsAsync(ConnectorName, secrets, ct: ct);
    }

    private async Task ClearSessionAsync(CancellationToken ct)
    {
        oauth.InvalidateToken();
        var secrets = await connectorConfigurations.GetSecretsAsync(ConnectorName, ct);
        secrets.Remove("refreshToken");
        secrets.Remove("grantedScopes");
        await connectorConfigurations.SaveSecretsAsync(ConnectorName, secrets, ct: ct);
    }

    private async Task ConsumeImportFromAsync(CancellationToken ct)
    {
        var stored = await connectorConfigurations.GetConfigurationAsync(ConnectorName, ct);
        if (stored is null) return;
        using var document = JsonDocument.Parse(stored.Configuration.RootElement.GetRawText());
        var configuration = document.RootElement.Deserialize<Dictionary<string, JsonElement>>() ?? [];
        configuration["importFrom"] = JsonSerializer.SerializeToElement<string?>(null);
        using var updated = JsonSerializer.SerializeToDocument(configuration);
        await connectorConfigurations.SaveConfigurationAsync(ConnectorName, updated, ct: ct);
    }

    /// <summary>
    ///     Collapses near-continuous raw heart-rate samples into one representative average-bpm
    ///     reading per UTC minute, keyed by a stable per-minute identifier so a re-import of the same
    ///     day updates the same aggregated record instead of accumulating duplicates.
    /// </summary>
    private readonly record struct HeartRateBucket(
        GoogleHealthReading Representative,
        decimal Sum,
        int Count);

    private static void AddHeartRateBuckets(
        IDictionary<long, HeartRateBucket> buckets,
        IEnumerable<GoogleHealthReading> readings)
    {
        const long bucketMillis = 60_000L;
        foreach (var reading in readings)
        {
            var bucket = reading.Mills - (reading.Mills % bucketMillis);
            if (buckets.TryGetValue(bucket, out var existing))
            {
                buckets[bucket] = existing with
                {
                    Sum = existing.Sum + reading.Value,
                    Count = existing.Count + 1
                };
            }
            else
            {
                buckets[bucket] = new HeartRateBucket(reading, reading.Value, 1);
            }
        }
    }

    private static List<GoogleHealthReading> MaterializeHeartRateBuckets(
        IReadOnlyDictionary<long, HeartRateBucket> buckets) =>
        buckets
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                var (bucket, aggregate) = (pair.Key, pair.Value);
                return new GoogleHealthReading
                {
                    DataType = aggregate.Representative.DataType,
                    OriginalId = $"minute:{bucket}",
                    Mills = bucket,
                    UtcOffsetMinutes = aggregate.Representative.UtcOffsetMinutes,
                    Value = Math.Round(aggregate.Sum / aggregate.Count, MidpointRounding.AwayFromZero),
                    Unit = aggregate.Representative.Unit
                };
            })
            .ToList();

    private static void AddCount(SyncResult result, string type, int count) =>
        result.ItemsSynced[GoogleHealthClient.TryGetSyncDataType(type, out var dataType)
            ? dataType
            : throw new GoogleHealthException("unsupported_type")] =
            result.ItemsSynced.GetValueOrDefault(dataType) + count;

    private static SyncResult Complete(SyncResult result, string message = "")
    {
        result.Success = true;
        result.Message = message;
        return result;
    }

    private static SyncResult Fail(SyncResult result, string code)
    {
        result.Success = false;
        result.Message = code;
        result.Errors.Add(code);
        return result;
    }

    private void LogFailure(Exception ex, GoogleHealthException error, Guid tenantId) => logger.LogError(ex,
        "Google Health import failed for tenant {TenantId} with code {Code} at stage {Stage} for data type {DataType}; provider status {ProviderStatus}, provider reason {ProviderReason}",
        tenantId, error.Message, error.Stage, error.DataType, error.ProviderStatus, error.ProviderReason);
}

public static class GoogleHealthErrorCode
{
    public static string Encode(string code, IEnumerable<string>? dataTypes = null)
    {
        var types = dataTypes?
            .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        return types.Length == 0 ? code : $"{code}:{string.Join(',', types)}";
    }

    public static (string? Code, string[] DataTypes) Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, []);
        // ConnectorSyncService uses the same tenant-wide slot for scheduled and admin-triggered
        // work. This is an expected overlap, not a user-visible Google failure.
        if (value.Contains("already running", StringComparison.OrdinalIgnoreCase))
            return (null, []);
        var separator = value.IndexOf(':');
        if (separator < 0) return (value, []);
        return (value[..separator], value[(separator + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(type => GoogleHealthClient.SupportedTypes.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }
}
