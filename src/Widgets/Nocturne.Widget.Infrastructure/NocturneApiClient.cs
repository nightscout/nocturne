using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Infrastructure;

/// <summary>
/// Implementation of the Nocturne API client providing HTTP and SignalR connectivity.
/// Uses OAuth Bearer tokens for authentication with automatic token refresh.
/// </summary>
public class NocturneApiClient : INocturneApiClient, IAsyncDisposable
{
    private readonly ICredentialStore _credentialStore;
    private readonly IOAuthService _oauthService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<NocturneApiClient> _logger;
    private HubConnection? _dataHubConnection;
    private HubConnection? _alarmHubConnection;
    private bool _disposed;

    /// <summary>Client identifier sent with the data-hub authorization request.</summary>
    private const string ClientName = "Nocturne.Widget.Windows11";

    /// <summary>Notification level (URGENT) at or above which an alarm is treated as urgent.</summary>
    private const int UrgentAlarmLevel = 2;

    /// <summary>
    /// Record collections the data hub subscribes to: the native V4 categories (glucose/care/device)
    /// plus the legacy v1 collections, so realtime works whether the tenant writes V4 or legacy shapes.
    /// </summary>
    private static readonly string[] SubscribedCollections =
        ["glucose", "care", "device", "entries", "treatments", "devicestatus"];

    /// <summary>
    /// Initializes a new instance of the NocturneApiClient
    /// </summary>
    /// <param name="credentialStore">The credential store for authentication</param>
    /// <param name="oauthService">The OAuth service for token management</param>
    /// <param name="httpClient">The HTTP client for API requests</param>
    /// <param name="logger">The logger instance</param>
    public NocturneApiClient(
        ICredentialStore credentialStore,
        IOAuthService oauthService,
        HttpClient httpClient,
        ILogger<NocturneApiClient> logger
    )
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public event EventHandler<DataUpdateEventArgs>? DataUpdated;

    /// <inheritdoc />
    public event EventHandler<AlarmEventArgs>? AlarmReceived;

    /// <inheritdoc />
    public event EventHandler<AlarmEventArgs>? AlarmCleared;

    /// <inheritdoc />
    public Task<V4SummaryResponse?> GetSummaryAsync(
        int hours = 0,
        bool includePredictions = false
    ) => GetJsonAsync<V4SummaryResponse>(
        $"/api/v4/summary?hours={hours}&includePredictions={includePredictions}"
    );

    /// <inheritdoc />
    public async Task<string?> GetSummaryRawAsync(int hours = 0, bool includePredictions = false)
    {
        try
        {
            var response = await SendWithRefreshAsync(
                $"/api/v4/summary?hours={hours}&includePredictions={includePredictions}"
            );
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching raw summary from Nocturne API");
            return null;
        }
    }

    /// <inheritdoc />
    public Task<MultiPeriodStatistics?> GetStatisticsAsync() =>
        GetJsonAsync<MultiPeriodStatistics>("/api/v4/statistics/periods");

    /// <inheritdoc />
    public Task<DeviceAgesResponse?> GetDeviceAgesAsync() =>
        GetJsonAsync<DeviceAgesResponse>("/api/v4/deviceage/all");

    /// <inheritdoc />
    public Task<PaginatedResponse<ApsSnapshot>?> GetLoopStatusAsync() =>
        GetJsonAsync<PaginatedResponse<ApsSnapshot>>("/api/v4/device-status/aps?limit=1&sort=timestamp_desc");

    /// <summary>
    /// Issues an authenticated GET to <paramref name="path"/> and deserializes the JSON body,
    /// transparently refreshing the access token once on a 401. Returns default on any failure.
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(string path)
    {
        try
        {
            var response = await SendWithRefreshAsync(path);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching {Path} from Nocturne API", path);
            return default;
        }
    }

    /// <summary>
    /// Issues an authorized GET, transparently refreshing the access token once and retrying on a
    /// 401. The (possibly still-401) response is returned for the caller to status-check.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRefreshAsync(string path)
    {
        var response = await SendAuthorizedGetAsync(path);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogDebug("Received 401, attempting token refresh");
            var refreshResult = await _oauthService.RefreshTokenAsync();
            if (!refreshResult.Success)
            {
                _logger.LogWarning("API request failed after token refresh attempt");
                return response;
            }

            response = await SendAuthorizedGetAsync(path);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendAuthorizedGetAsync(string path)
    {
        if (!await _oauthService.EnsureValidTokenAsync())
        {
            _logger.LogWarning("No valid credentials available for API request");
            throw new InvalidOperationException("No valid credentials available");
        }

        var credentials = await _credentialStore.GetCredentialsAsync()
            ?? throw new InvalidOperationException("No credentials available after token validation");

        var baseUrl = credentials.ApiUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

        return await _httpClient.SendAsync(request);
    }

    /// <inheritdoc />
    public async Task ConnectSignalRAsync()
    {
        if (_dataHubConnection is not null || _alarmHubConnection is not null)
        {
            _logger.LogWarning("SignalR connection already exists");
            return;
        }

        // Ensure we have a valid token
        if (!await _oauthService.EnsureValidTokenAsync())
        {
            _logger.LogWarning("No valid credentials available for SignalR connection");
            return;
        }

        var credentials = await _credentialStore.GetCredentialsAsync();
        if (credentials is null)
        {
            _logger.LogWarning("No credentials available for SignalR connection");
            return;
        }

        var baseUrl = credentials.ApiUrl.TrimEnd('/');

        try
        {
            // DataHub carries glucose/care/device record events (create/update/delete), legacy entries
            // (dataUpdate), and tracker state; AlarmHub carries alarm notifications. The widget re-fetches
            // over HTTP on any signal, so each event collapses to a single "refresh now" trigger.
            _dataHubConnection = BuildHubConnection($"{baseUrl}/hubs/data", "data");
            ConfigureDataHubHandlers(_dataHubConnection);
            // Group membership is per-connection, so a full reconnect must re-authorize and re-subscribe,
            // then re-fetch once to recover any pushes missed during the reconnect gap.
            _dataHubConnection.Reconnected += async _ =>
            {
                await JoinDataGroupsAsync();
                RaiseDataUpdated("reconnect");
            };

            _alarmHubConnection = BuildHubConnection($"{baseUrl}/hubs/alarms", "alarm");
            ConfigureAlarmHubHandlers(_alarmHubConnection);
            _alarmHubConnection.Reconnected += async _ => await JoinAlarmGroupAsync();

            await _dataHubConnection.StartAsync();
            await JoinDataGroupsAsync();

            await _alarmHubConnection.StartAsync();
            await JoinAlarmGroupAsync();

            _logger.LogInformation("SignalR connections established");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SignalR hubs");
            await DisconnectSignalRAsync();
            throw;
        }
    }

    /// <summary>
    /// Builds a hub connection that authenticates with the current OAuth access token and reconnects
    /// automatically. <paramref name="label"/> only tags log messages.
    /// </summary>
    private HubConnection BuildHubConnection(string url, string label)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(url, options => options.AccessTokenProvider = GetAccessTokenAsync)
            .WithAutomaticReconnect()
            .Build();

        connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "SignalR {Hub} hub connection lost, reconnecting", label);
            return Task.CompletedTask;
        };

        connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("SignalR {Hub} hub reconnected ({ConnectionId})", label, connectionId);
            return Task.CompletedTask;
        };

        connection.Closed += error =>
        {
            if (error is not null)
            {
                _logger.LogError(error, "SignalR {Hub} hub connection closed with error", label);
            }
            else
            {
                _logger.LogInformation("SignalR {Hub} hub connection closed", label);
            }
            return Task.CompletedTask;
        };

        return connection;
    }

    /// <summary>Resolves a fresh access token for the SignalR handshake, refreshing if needed.</summary>
    private async Task<string?> GetAccessTokenAsync()
    {
        await _oauthService.EnsureValidTokenAsync();
        var creds = await _credentialStore.GetCredentialsAsync();
        return creds?.AccessToken;
    }

    /// <summary>
    /// Authorizes the data-hub connection (joins the tenant "authorized" group for legacy entries and
    /// tracker events) and subscribes to the V4/legacy record collection groups. Both hub methods
    /// authenticate in-band from the supplied access token.
    /// </summary>
    private async Task JoinDataGroupsAsync()
    {
        if (_dataHubConnection is null)
        {
            return;
        }

        try
        {
            var token = await GetAccessTokenAsync();
            // Authorize joins the tenant "authorized" group and reports success in-band. A failure here
            // (e.g. a token that failed to refresh) leaves the connection alive but subscribed to nothing,
            // so surface it rather than silently degrading to poll-only.
            var authorized = await _dataHubConnection.InvokeAsync<JsonElement>(
                "Authorize",
                new { client = ClientName, token }
            );
            if (!GetBool(authorized, "success"))
            {
                _logger.LogWarning("Data hub rejected authorization; realtime updates will not be received");
            }

            await _dataHubConnection.InvokeAsync("Subscribe", new { collections = SubscribedCollections });
        }
        catch (HubException ex)
        {
            _logger.LogWarning(ex, "Failed to authorize/subscribe on the data hub");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to authorize/subscribe on the data hub");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to authorize/subscribe on the data hub");
        }
    }

    /// <summary>Subscribes the alarm-hub connection to the tenant alarm-subscribers group.</summary>
    private async Task JoinAlarmGroupAsync()
    {
        if (_alarmHubConnection is null)
        {
            return;
        }

        try
        {
            var token = await GetAccessTokenAsync();
            var subscribed = await _alarmHubConnection.InvokeAsync<JsonElement>(
                "Subscribe",
                new { jwtToken = token }
            );
            if (!GetBool(subscribed, "success"))
            {
                _logger.LogWarning("Alarm hub rejected subscription; alarm updates will not be received");
            }
        }
        catch (HubException ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe on the alarm hub");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe on the alarm hub");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe on the alarm hub");
        }
    }

    /// <inheritdoc />
    public async Task DisconnectSignalRAsync()
    {
        await DisposeHubAsync(_dataHubConnection);
        _dataHubConnection = null;

        await DisposeHubAsync(_alarmHubConnection);
        _alarmHubConnection = null;
    }

    private async Task DisposeHubAsync(HubConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting from SignalR hub");
        }
    }

    private void ConfigureDataHubHandlers(HubConnection connection)
    {
        // Legacy v1 entries broadcast (tenant "authorized" group).
        connection.On<JsonElement>("dataUpdate", _ => RaiseDataUpdated("entries"));

        // Native V4 record events (glucose/care/device groups); the payload is { recordType, doc }.
        connection.On<JsonElement>("create", payload => RaiseDataUpdated(RecordType(payload)));
        connection.On<JsonElement>("update", payload => RaiseDataUpdated(RecordType(payload)));
        connection.On<JsonElement>("delete", payload => RaiseDataUpdated(RecordType(payload)));

        // Device-age tracker recomputation (drives the Ages widget).
        connection.On<JsonElement>("trackerUpdate", _ => RaiseDataUpdated("tracker"));
    }

    private void ConfigureAlarmHubHandlers(HubConnection connection)
    {
        connection.On<JsonElement>("alarm", payload => RaiseAlarm(payload, urgent: false, cleared: false));
        connection.On<JsonElement>("urgent_alarm", payload => RaiseAlarm(payload, urgent: true, cleared: false));
        connection.On<JsonElement>("clear_alarm", payload => RaiseAlarm(payload, urgent: false, cleared: true));
    }

    private void RaiseDataUpdated(string dataType) =>
        DataUpdated?.Invoke(
            this,
            new DataUpdateEventArgs
            {
                DataType = dataType,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );

    private void RaiseAlarm(JsonElement payload, bool urgent, bool cleared)
    {
        var level = GetInt(payload, "level");
        var args = new AlarmEventArgs
        {
            AlarmId = GetString(payload, "group"),
            Title = GetString(payload, "title"),
            Message = GetString(payload, "message"),
            Level = level,
            Urgent = urgent || level >= UrgentAlarmLevel,
            Timestamp = GetLong(payload, "timestamp"),
        };

        if (cleared)
        {
            AlarmCleared?.Invoke(this, args);
        }
        else
        {
            AlarmReceived?.Invoke(this, args);
        }
    }

    /// <summary>Reads the V4 <c>recordType</c> discriminator from a create/update/delete payload.</summary>
    private static string RecordType(JsonElement payload) =>
        TryGetProperty(payload, "recordType", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "unknown"
            : "unknown";

    private static string GetString(JsonElement obj, string name) =>
        TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement obj, string name) =>
        TryGetProperty(obj, name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static long GetLong(JsonElement obj, string name) =>
        TryGetProperty(obj, name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static bool GetBool(JsonElement obj, string name) =>
        TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Case-insensitive property lookup, tolerant of SignalR's JSON casing.</summary>
    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Disposes the API client and releases resources
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectSignalRAsync();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
