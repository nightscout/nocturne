using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Mappers;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyFitnessPal.Services;

/// <summary>
/// Connector service for MyFitnessPal food diary data.
/// Reads the authenticated user's own diary through the mobile Apollo "query-envoy" GraphQL API,
/// which works for private diaries and is reachable where the <c>www</c> host is Cloudflare-blocked.
/// </summary>
public class MyFitnessPalConnectorService : BaseConnectorService<MyFitnessPalConnectorConfiguration>
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly MyFitnessPalAuthTokenProvider _tokenProvider;
    private readonly IConnectorConfigurationService _configService;
    private readonly IConnectorPublisher? _connectorPublisher;
    private readonly MyFitnessPalFoodEntryMapper _mapper;

    private string? _accessToken;
    private string? _userId;

    public MyFitnessPalConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<MyFitnessPalConnectorConfiguration> serverResolver,
        ILogger<MyFitnessPalConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        MyFitnessPalAuthTokenProvider tokenProvider,
        IConnectorConfigurationService configService,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _connectorPublisher = publisher;
        _mapper = new MyFitnessPalFoodEntryMapper(logger);
    }

    protected override string ConnectorSource => DataSources.MyFitnessPalConnector;
    public override string ServiceName => "MyFitnessPal";
    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Food];

    /// <inheritdoc />
    public override Task<bool> AuthenticateAsync()
    {
        // Legacy method; actual auth happens per-tenant in PerformSyncInternalAsync
        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    public override Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        // MFP doesn't provide glucose data
        return Task.FromResult(Enumerable.Empty<Entry>());
    }

    /// <summary>
    /// Entry point for the scheduled sync.
    /// </summary>
    /// <remarks>
    /// The base implementation derives the window from the tenant's most recent glucose reading,
    /// which says nothing about how far back the food diary needs reading — with a live CGM it
    /// resolves to roughly now, and nothing would ever be imported. The food diary's own window is
    /// <see cref="MyFitnessPalConnectorConfiguration.LookbackDays"/>.
    /// </remarks>
    public override Task<SyncResult> SyncDataAsync(
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null
    ) =>
        base.SyncDataAsync(
            config,
            cancellationToken,
            since ?? DateTime.UtcNow.AddDays(-config.LookbackDays),
            progressReporter);

    /// <inheritdoc />
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        // This override replaces the base's data-type dispatch, so the toggle it would have
        // honoured has to be checked here.
        if (!config.GetEnabledDataTypes(SupportedDataTypes).Contains(SyncDataType.Food))
        {
            _logger.LogInformation(
                "[{ConnectorSource}] Food sync is disabled; nothing to do", ConnectorSource);
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        if (!await AuthenticateWithConfigAsync(config, cancellationToken))
        {
            result.Success = false;
            result.Errors.Add("Authentication failed for MyFitnessPal");
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        // The request carries UTC instants; pin the kind so the window is not reinterpreted as local.
        var from = AsUtc(request.From ?? DateTime.UtcNow.AddDays(-config.LookbackDays));

        // A background sync leaves the end open on purpose: clamping to "now" would discard
        // entries pre-logged for later today, whose resolved time is still in the future. The
        // diary does allow logging arbitrarily far ahead though, so keep a day's headroom rather
        // than importing next week's plan as if it had been eaten.
        var to = request.To.HasValue
            ? AsUtc(request.To.Value)
            : AsUtc(DateTime.UtcNow.AddDays(1));

        var entries = await FetchDiaryAsync(config, from, cancellationToken);
        if (entries == null)
        {
            result.Success = false;
            result.Errors.Add("Failed to fetch diary data from MyFitnessPal");
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        var mealNames = await ResolveMealNamesAsync(entries, from, to, cancellationToken);

        var foodEntryImports = _mapper.Map(entries, config, from, to, mealNames);
        var count = foodEntryImports.Count;

        if (count > 0)
        {
            if (_connectorPublisher is not { IsAvailable: true })
            {
                _logger.LogWarning("Publisher not available for connector food entry submission");
                result.Success = false;
                result.Errors.Add("Publisher not available");
            }
            else
            {
                var imported = await _connectorPublisher.Metadata.PublishConnectorFoodEntriesAsync(
                    foodEntryImports,
                    ConnectorSource, WriteOrigin.Live,
                    cancellationToken
                ); // Food is a dormant broadcast category — origin irrelevant until wired.
                if (imported == null)
                {
                    result.Success = false;
                    result.Errors.Add("Failed to publish food entries");
                }
            }
        }

        result.ItemsSynced[SyncDataType.Food] = count;
        _logger.LogInformation(
            "[{ConnectorSource}] Synced {Count} food entries from MyFitnessPal since {From:yyyy-MM-dd}",
            ConnectorSource,
            count,
            from
        );

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>
    /// Obtains an access token and the MyFitnessPal user id required by the GraphQL API.
    /// </summary>
    private async Task<bool> AuthenticateWithConfigAsync(
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            TrackFailedRequest("Failed to obtain MyFitnessPal access token");
            return false;
        }

        var session = await _tokenProvider.GetCachedSessionAsync();
        var metadata = session?.Metadata;

        _accessToken = token;
        _userId = metadata?.GetValueOrDefault(MyFitnessPalAuthTokenProvider.UserIdMetadataKey)
                  ?? config.UserId;

        if (string.IsNullOrEmpty(_userId))
        {
            TrackFailedRequest("MyFitnessPal did not return a user id");
            return false;
        }

        config.UserId = _userId;
        var refreshToken = metadata?.GetValueOrDefault(MyFitnessPalAuthTokenProvider.RefreshTokenMetadataKey);
        if (!string.IsNullOrEmpty(refreshToken))
            config.RefreshToken = refreshToken;

        // Persist here rather than at the end of the sync. MyFitnessPal invalidates the old
        // refresh token as soon as it issues a new one, so letting a later failure skip the write
        // would leave an account configured without a password unable to authenticate again.
        await PersistSecretsIfChangedAsync(config, cancellationToken);

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    /// Reads the diary backwards from the newest entry until the requested window is covered.
    /// </summary>
    /// <remarks>
    /// Deliberately not an incremental sync-down. Meal names are recovered by reconciling a day's
    /// entries against that day's totals from the legacy diary, which only works given the whole
    /// day: a cursor-driven delta would deliver a day in pieces and never reconcile. Re-reading the
    /// window each sync also means an entry that is edited, or that falls outside the window and
    /// later moves into it, is picked up rather than being passed over for good.
    /// </remarks>
    private async Task<List<MfpFoodDiaryEntryNode>?> FetchDiaryAsync(
        MyFitnessPalConnectorConfiguration config,
        DateTimeOffset from,
        CancellationToken cancellationToken)
    {
        var entries = new List<MfpFoodDiaryEntryNode>();
        var windowStart = DateOnly.FromDateTime(from.UtcDateTime.Date);
        string? before = null;
        var preWindowPages = 0;

        for (var page = 0; page < MyFitnessPalConstants.MaxPagesPerSync; page++)
        {
            var cursor = before;
            var connection = await ExecuteWithRetryAsync(
                async () => await FetchDiaryPageAsync(cursor, cancellationToken),
                _retryDelayStrategy,
                maxRetries: config.MaxRetryAttempts,
                operationName: "FetchMyFitnessPalDiaryPage",
                cancellationToken: cancellationToken
            );

            if (connection == null)
                return null;

            DateOnly? newestOnPage = null;
            foreach (var edge in connection.FoodDiaryEntryEdges)
            {
                var node = edge.FoodDiaryEntryNode;
                if (node == null)
                    continue;

                // Deleted entries and non-active states carry none of the ActiveFoodDiaryEntry fields.
                if (string.Equals(edge.FoodDiaryEntryEdgeSync?.Operation, "DELETE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!DateOnly.TryParse(node.Date, CultureInfo.InvariantCulture, out var entryDate))
                    continue;

                entries.Add(node);
                if (newestOnPage == null || entryDate > newestOnPage)
                    newestOnPage = entryDate;
            }

            before = connection.FoodDiaryEntryPaging?.StartCursor;
            if (connection.FoodDiaryEntryPaging?.HasPreviousPage != true || string.IsNullOrEmpty(before))
                return entries;

            // Pages run newest first, but their order tracks modification rather than diary date,
            // so a block of recently edited old entries can sit ahead of the window. Read a few
            // pages past the first that falls entirely before the window rather than stopping at
            // it. A page with no dated entries — an all-deletions page — is evidence either way,
            // so it neither ends the walk nor resets the count.
            if (newestOnPage == null)
                continue;

            preWindowPages = newestOnPage >= windowStart ? 0 : preWindowPages + 1;
            if (preWindowPages >= MyFitnessPalConstants.PreWindowPageLookahead)
                return entries;
        }

        _logger.LogWarning(
            "[{ConnectorSource}] Stopped after {MaxPages} pages without reaching {WindowStart}",
            ConnectorSource,
            MyFitnessPalConstants.MaxPagesPerSync,
            windowStart);

        return entries;
    }

    /// <summary>
    /// Resolves a meal name for each entry falling inside the sync window.
    /// </summary>
    /// <remarks>
    /// The GraphQL sync carries no meal, so the legacy diary is fetched for each day that has
    /// entries and the two are reconciled per day. A day the reconciliation cannot settle is left
    /// unnamed rather than guessed at, and a failed request only costs that day its meal names.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> ResolveMealNamesAsync(
        List<MfpFoodDiaryEntryNode> entries,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>();

        var days = entries
            .Where(e => DateOnly.TryParse(e.Date, CultureInfo.InvariantCulture, out var d)
                        && d >= DateOnly.FromDateTime(from.UtcDateTime.Date)
                        && d <= DateOnly.FromDateTime(to.UtcDateTime.Date))
            .GroupBy(e => e.Date!)
            .ToList();

        if (days.Count > MyFitnessPalConstants.MaxDiaryDaysPerSync)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] {Days} days in window exceeds the {Max} day meal-name budget; importing without meal names",
                ConnectorSource,
                days.Count,
                MyFitnessPalConstants.MaxDiaryDaysPerSync);
            return resolved;
        }

        foreach (var day in days)
        {
            var meals = await FetchDiaryMealsAsync(day.Key, cancellationToken);
            if (meals == null)
                continue;

            var attributed = MyFitnessPalMealAttributor.Attribute([.. day], meals);
            if (attributed.Count == 0)
            {
                _logger.LogDebug(
                    "[{ConnectorSource}] Could not attribute meals for {Date}; importing that day unnamed",
                    ConnectorSource,
                    day.Key);
                continue;
            }

            foreach (var (entryId, mealName) in attributed)
                resolved[entryId] = mealName;
        }

        return resolved;
    }

    private async Task<List<MfpDiaryItem>?> FetchDiaryMealsAsync(
        string date,
        CancellationToken cancellationToken)
    {
        var url = $"{MyFitnessPalConstants.Servers.Auth}{MyFitnessPalConstants.Endpoints.Diary}"
                  + $"?entry_date={Uri.EscapeDataString(date)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeaders(request);

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "MyFitnessPal diary for {Date} returned HTTP {StatusCode}",
                    date,
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<MfpDiaryResponse>(body);

            return parsed?.Items
                .Where(i => string.Equals(i.Type, "diary_meal", StringComparison.Ordinal))
                .ToList();
        }
        // Losing one day costs that day its meal names; it must never fail the whole sync.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            // An HttpClient timeout surfaces as TaskCanceledException without the token being set.
            _logger.LogWarning(ex, "MyFitnessPal diary request for {Date} timed out", date);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MyFitnessPal diary request for {Date} failed", date);
            return null;
        }
        catch (JsonException ex)
        {
            // A WAF interstitial served with a 200 parses as anything but the expected document.
            _logger.LogWarning(ex, "MyFitnessPal diary for {Date} was not valid JSON", date);
            return null;
        }
    }

    private async Task<MfpFoodDiaryEntryConnection?> FetchDiaryPageAsync(
        string? before,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            operationName = MyFitnessPalConstants.SyncFoodDiaryEntriesOperationName,
            query = MyFitnessPalConstants.SyncFoodDiaryEntriesDocument,
            variables = BuildVariables(before),
        };

        var url = $"{MyFitnessPalConstants.Servers.GraphQl}{MyFitnessPalConstants.Endpoints.GraphQl}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        AddAuthHeaders(request);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Drop the cached token so the next sync re-authenticates. Without this a token the
            // server has already rejected stays cached for its full nominal lifetime. Only on 401:
            // a 403 here is more likely a WAF block, and re-minting a good token every cycle
            // risks MyFitnessPal rate-limiting the login itself.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                _tokenProvider.InvalidateToken();

            _logger.LogError(
                "MyFitnessPal GraphQL returned HTTP {StatusCode}: {ResponseBody}",
                (int)response.StatusCode,
                body.Length > 500 ? body[..500] : body);
            throw new HttpRequestException(
                $"MyFitnessPal GraphQL returned HTTP {(int)response.StatusCode}",
                null,
                response.StatusCode);
        }

        var parsed = JsonSerializer.Deserialize<MfpGraphQlResponse<MfpBatchSyncData>>(body);

        if (parsed?.Errors is { Count: > 0 } errors)
        {
            _logger.LogError(
                "MyFitnessPal GraphQL returned errors: {Errors}",
                string.Join("; ", errors.Select(e => e.Message)));
            return null;
        }

        return parsed?.Data?.BatchSync?.FoodDiaryEntryConnection;
    }

    /// <summary>
    /// Builds the <c>batchSync</c> input for a page read backwards from the newest entry.
    /// Absent values are omitted rather than sent as null, matching how the mobile client
    /// serializes its optional inputs. <c>syncCursors</c> is required even when empty.
    /// </summary>
    public static Dictionary<string, object?> BuildVariables(string? before)
    {
        var syncCursors = new Dictionary<string, object?>();

        var pagination = new Dictionary<string, object?> { ["last"] = MyFitnessPalConstants.PageSize };
        if (!string.IsNullOrEmpty(before))
            pagination["before"] = before;

        return new Dictionary<string, object?>
        {
            ["input"] = new Dictionary<string, object?>
            {
                ["syncResources"] = new Dictionary<string, object?>
                {
                    ["foodDiaryEntrySyncResource"] = new Dictionary<string, object?>
                    {
                        ["paginationInput"] = pagination,
                        ["syncCursors"] = syncCursors,
                    },
                },
            },
        };
    }

    /// <summary>
    /// Applies the headers both MyFitnessPal hosts require. <c>client-metadata</c> is mandatory:
    /// without it the GraphQL endpoint rejects the request after validation, not before, which
    /// makes the failure look like a schema problem.
    /// </summary>
    private void AddAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");
        request.Headers.TryAddWithoutValidation(MyFitnessPalConstants.Headers.UserId, _userId);
        request.Headers.TryAddWithoutValidation(
            MyFitnessPalConstants.Headers.ClientId, MyFitnessPalConstants.ClientId);
        request.Headers.TryAddWithoutValidation(
            MyFitnessPalConstants.Headers.ClientMetadata, BuildClientMetadata());
    }

    private static string BuildClientMetadata()
    {
        var json =
            $"{{\"device-id\": \"{DeviceId}\",\"app-version\": \"{MyFitnessPalConstants.AppVersion}\"}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Stable per-process device identifier; MyFitnessPal only echoes it back in telemetry.
    /// </summary>
    private static readonly string DeviceId = Guid.NewGuid().ToString();

    /// <summary>
    /// Writes back the values MyFitnessPal rotates or derives. Secrets are stored as a single
    /// document, so the existing entries are read first and merged rather than replaced.
    /// </summary>
    private async Task PersistSecretsIfChangedAsync(
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        try
        {
            var updates = new Dictionary<string, string?>
            {
                ["refreshToken"] = config.RefreshToken,
                ["userId"] = config.UserId,
            };

            if (await _configService.MergeSecretsAsync(
                    "MyFitnessPal", updates, "connector-runtime", _logger, cancellationToken))
                _logger.LogInformation("[{ConnectorSource}] Persisted updated connector state", ConnectorSource);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to persist connector state", ConnectorSource);
        }
    }

}
