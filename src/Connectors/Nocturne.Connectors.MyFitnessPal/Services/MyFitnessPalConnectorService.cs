using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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

    /// <inheritdoc />
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!await AuthenticateWithConfigAsync(config, cancellationToken))
        {
            result.Success = false;
            result.Errors.Add("Authentication failed for MyFitnessPal");
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        // The request carries UTC instants; pin the kind so the window is not reinterpreted as local.
        var from = AsUtc(request.From ?? DateTime.UtcNow.AddDays(-config.LookbackDays));
        var to = AsUtc(request.To ?? DateTime.UtcNow);

        var sync = await FetchDiaryAsync(config, cancellationToken);
        if (sync == null)
        {
            result.Success = false;
            result.Errors.Add("Failed to fetch diary data from MyFitnessPal");
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        var foodEntryImports = _mapper.Map(sync.Entries, config, from, to);
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

        if (result.Success)
        {
            // A finished walk advances the sync cursor and clears the resume point; an unfinished
            // one does the opposite, so the next run picks up mid-walk rather than starting over.
            config.PageCursor = sync.PageCursor;
            if (sync.PageCursor == null)
                config.SyncCursor = sync.EndSyncCursor;
        }

        await PersistSecretsIfChangedAsync(config, cancellationToken);

        result.ItemsSynced[SyncDataType.Food] = count;
        _logger.LogInformation(
            "[{ConnectorSource}] Synced {Count} food entries from MyFitnessPal ({From:yyyy-MM-dd} to {To:yyyy-MM-dd})",
            ConnectorSource,
            count,
            from,
            to
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

        // Fold the freshly minted values back into the config so PersistSecretsIfChangedAsync
        // can compare them against what is stored.
        config.UserId = _userId;
        var refreshToken = metadata?.GetValueOrDefault(MyFitnessPalAuthTokenProvider.RefreshTokenMetadataKey);
        if (!string.IsNullOrEmpty(refreshToken))
            config.RefreshToken = refreshToken;

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    /// Result of a diary sync-down walk.
    /// </summary>
    /// <param name="Entries">Entries collected across all pages, deletions excluded.</param>
    /// <param name="EndSyncCursor">Sync cursor to resume from once the walk has completed.</param>
    /// <param name="PageCursor">Page to resume at, non-null only when the walk is unfinished.</param>
    private sealed record DiarySync(
        List<MfpFoodDiaryEntryNode> Entries,
        string? EndSyncCursor,
        string? PageCursor);

    /// <summary>
    /// Walks the food diary sync-down connection, following page cursors until exhausted or the
    /// per-run page cap is reached. A capped run reports where it stopped so the next run resumes
    /// there; the sync cursor only advances once the whole walk is done.
    /// </summary>
    private async Task<DiarySync?> FetchDiaryAsync(
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var entries = new List<MfpFoodDiaryEntryNode>();
        var endSyncCursor = config.SyncCursor;
        var pageCursor = config.PageCursor;
        var completed = false;

        for (var page = 0; page < MyFitnessPalConstants.MaxPagesPerSync; page++)
        {
            var cursor = pageCursor;
            var connection = await ExecuteWithRetryAsync(
                async () => await FetchDiaryPageAsync(config.SyncCursor, cursor, cancellationToken),
                _retryDelayStrategy,
                maxRetries: config.MaxRetryAttempts,
                operationName: "FetchMyFitnessPalDiaryPage",
                cancellationToken: cancellationToken
            );

            if (connection == null)
                return null;

            foreach (var edge in connection.FoodDiaryEntryEdges)
            {
                var node = edge.FoodDiaryEntryNode;
                if (node == null)
                    continue;

                // Deleted entries and non-active states carry none of the ActiveFoodDiaryEntry fields.
                if (string.Equals(edge.FoodDiaryEntryEdgeSync?.Operation, "DELETE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (node.Date == null)
                    continue;

                entries.Add(node);
            }

            if (connection.FoodDiaryEntrySyncInfo?.EndSyncCursor is { Length: > 0 } newCursor)
                endSyncCursor = newCursor;

            pageCursor = connection.FoodDiaryEntryPaging?.EndCursor;
            if (connection.FoodDiaryEntryPaging?.HasNextPage != true || string.IsNullOrEmpty(pageCursor))
            {
                completed = true;
                break;
            }
        }

        if (completed)
            return new DiarySync(entries, endSyncCursor, null);

        _logger.LogInformation(
            "[{ConnectorSource}] Reached the {MaxPages} page cap; resuming from this page next sync",
            ConnectorSource,
            MyFitnessPalConstants.MaxPagesPerSync);

        return new DiarySync(entries, endSyncCursor, pageCursor);
    }

    private async Task<MfpFoodDiaryEntryConnection?> FetchDiaryPageAsync(
        string? syncCursor,
        string? pageCursor,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            operationName = MyFitnessPalConstants.SyncFoodDiaryEntriesOperationName,
            query = MyFitnessPalConstants.SyncFoodDiaryEntriesDocument,
            variables = BuildVariables(syncCursor, pageCursor),
        };

        var url = $"{MyFitnessPalConstants.Servers.GraphQl}{MyFitnessPalConstants.Endpoints.GraphQl}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");
        request.Headers.TryAddWithoutValidation(MyFitnessPalConstants.Headers.UserId, _userId);
        request.Headers.TryAddWithoutValidation(
            MyFitnessPalConstants.Headers.ClientId, MyFitnessPalConstants.ClientId);
        request.Headers.TryAddWithoutValidation(
            MyFitnessPalConstants.Headers.ClientMetadata, BuildClientMetadata());

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
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
    /// Builds the <c>batchSync</c> input. Absent cursors are omitted rather than sent as null,
    /// matching how the mobile client serializes its optional inputs.
    /// </summary>
    public static Dictionary<string, object?> BuildVariables(string? syncCursor, string? pageCursor)
    {
        var syncCursors = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(syncCursor))
            syncCursors["startAfterSyncCursor"] = syncCursor;

        var pagination = new Dictionary<string, object?> { ["first"] = MyFitnessPalConstants.PageSize };
        if (!string.IsNullOrEmpty(pageCursor))
            pagination["after"] = pageCursor;

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
            var stored = await _configService.GetSecretsAsync("MyFitnessPal", cancellationToken);

            var changed = false;
            changed |= WriteSecret(stored, "refreshToken", config.RefreshToken);
            changed |= WriteSecret(stored, "userId", config.UserId);
            changed |= WriteSecret(stored, "syncCursor", config.SyncCursor);
            changed |= WriteSecret(stored, "pageCursor", config.PageCursor);

            if (!changed)
                return;

            await _configService.SaveSecretsAsync(
                "MyFitnessPal", stored, "connector-runtime", cancellationToken);
            _logger.LogInformation("[{ConnectorSource}] Persisted updated connector state", ConnectorSource);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to persist connector state", ConnectorSource);
        }
    }

    /// <summary>
    ///     Writes one secret, removing the key when the value is gone. Returns whether anything moved.
    /// </summary>
    public static bool WriteSecret(Dictionary<string, string> secrets, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return secrets.Remove(key);

        if (secrets.GetValueOrDefault(key) == value)
            return false;

        secrets[key] = value;
        return true;
    }

}
