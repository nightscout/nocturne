using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private HubConnection? _hubConnection;
    private bool _disposed;

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
    public event EventHandler<TrackerUpdateEventArgs>? TrackerUpdated;

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
        if (_hubConnection is not null)
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

        try
        {
            var hubUrl = $"{credentials.ApiUrl.TrimEnd('/')}/hubs/nocturne";

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(
                    hubUrl,
                    options =>
                    {
                        // Use Bearer token for SignalR authentication
                        options.AccessTokenProvider = async () =>
                        {
                            await _oauthService.EnsureValidTokenAsync();
                            var creds = await _credentialStore.GetCredentialsAsync();
                            return creds?.AccessToken;
                        };
                    }
                )
                .WithAutomaticReconnect()
                .Build();

            ConfigureHubHandlers(_hubConnection);

            await _hubConnection.StartAsync();
            _logger.LogInformation("SignalR connection established");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SignalR hub");
            _hubConnection = null;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisconnectSignalRAsync()
    {
        if (_hubConnection is null)
        {
            return;
        }

        try
        {
            await _hubConnection.StopAsync();
            await _hubConnection.DisposeAsync();
            _logger.LogInformation("SignalR connection closed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting from SignalR hub");
        }
        finally
        {
            _hubConnection = null;
        }
    }

    private void ConfigureHubHandlers(HubConnection connection)
    {
        connection.On<string, long>(
            "DataUpdated",
            (dataType, timestamp) =>
            {
                DataUpdated?.Invoke(
                    this,
                    new DataUpdateEventArgs { DataType = dataType, Timestamp = timestamp }
                );
            }
        );

        connection.On<string, string, double, double>(
            "TrackerUpdated",
            (trackerId, trackerName, ageHours, lifespanHours) =>
            {
                TrackerUpdated?.Invoke(
                    this,
                    new TrackerUpdateEventArgs
                    {
                        TrackerId = trackerId,
                        TrackerName = trackerName,
                        AgeHours = ageHours,
                        LifespanHours = lifespanHours,
                    }
                );
            }
        );

        connection.On<string, string, string, int, bool, long>(
            "AlarmReceived",
            (alarmId, title, message, level, urgent, timestamp) =>
            {
                AlarmReceived?.Invoke(
                    this,
                    new AlarmEventArgs
                    {
                        AlarmId = alarmId,
                        Title = title,
                        Message = message,
                        Level = level,
                        Urgent = urgent,
                        Timestamp = timestamp,
                    }
                );
            }
        );

        connection.On<string, string, string, int, bool, long>(
            "AlarmCleared",
            (alarmId, title, message, level, urgent, timestamp) =>
            {
                AlarmCleared?.Invoke(
                    this,
                    new AlarmEventArgs
                    {
                        AlarmId = alarmId,
                        Title = title,
                        Message = message,
                        Level = level,
                        Urgent = urgent,
                        Timestamp = timestamp,
                    }
                );
            }
        );

        connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "SignalR connection lost, attempting to reconnect");
            return Task.CompletedTask;
        };

        connection.Reconnected += connectionId =>
        {
            _logger.LogInformation(
                "SignalR reconnected with connection ID {ConnectionId}",
                connectionId
            );
            return Task.CompletedTask;
        };

        connection.Closed += error =>
        {
            if (error is not null)
            {
                _logger.LogError(error, "SignalR connection closed with error");
            }
            else
            {
                _logger.LogInformation("SignalR connection closed");
            }
            return Task.CompletedTask;
        };
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
