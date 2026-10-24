using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Widget;
using Nocturne.Desktop.Tray.Extensions;
using Nocturne.Widget.Contracts;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// SDK + SignalR client for the Nocturne API.
/// Authenticates via Bearer tokens managed by OidcAuthService.
/// Real-time data flows through the SignalR DataHub with HTTP polling as fallback; alarms arrive
/// on the AlarmHub.
/// </summary>
public sealed class NocturneClient : IAsyncDisposable
{
    private readonly SettingsService _settingsService;
    private readonly OidcAuthService _authService;
    private readonly ILogger _logger;
    private HubConnection? _hubConnection;
    private HubConnection? _alarmHubConnection;
    private CancellationTokenSource? _pollCts;
    private bool _isConnected;

    public event Action<V4GlucoseReading>? OnGlucoseReading;
    public event Action<AlarmEventArgs>? OnAlarm;
    public event Action<bool>? OnConnectionChanged;
    public event Action? OnReconnected;

    public bool IsConnected => _isConnected;

    public NocturneClient(SettingsService settingsService, OidcAuthService authService, ILogger<NocturneClient> logger)
    {
        _settingsService = settingsService;
        _authService = authService;
        _logger = logger;

        _authService.AuthStateChanged += OnAuthStateChanged;
    }

    /// <summary>Connects, replacing any connection already open.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();

        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return;
        if (!_authService.IsAuthenticated) return;

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await ConnectSignalRAsync(serverUrl, _pollCts.Token);
        StartPolling(_pollCts.Token);
    }

    public async Task DisconnectAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }

        if (_alarmHubConnection is not null)
        {
            await _alarmHubConnection.DisposeAsync();
            _alarmHubConnection = null;
        }

        SetConnected(false);
    }

    /// <summary>
    /// Fetches the V4 summary including current reading and glucose history in a single call.
    /// Returns the current reading and an ordered list of historical readings for the chart.
    /// </summary>
    public async Task<(V4GlucoseReading? Current, IReadOnlyList<V4GlucoseReading> History)> FetchSummaryAsync(
        int hours = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var serverUrl = GetServerUrl();
            if (serverUrl is null) return (null, []);

            var token = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return (null, []);

            using var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var summary = await http.GetFromJsonAsync<V4SummaryResponse>(
                $"/api/v4/summary?hours={hours}&includePredictions=false",
                cancellationToken);

            if (summary is null) return (null, []);

            var current = summary.Current;
            var history = summary.History?.ToList() ?? [];

            if (current is not null && !history.Any(h => h.Mills == current.Mills))
            {
                history.Add(current);
            }
            history.Sort((a, b) => a.Mills.CompareTo(b.Mills));

            return (current, history);
        }
        catch (OperationCanceledException)
        {
            return (null, []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch V4 summary");
            return (null, []);
        }
    }

    private string? GetServerUrl()
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        return string.IsNullOrEmpty(serverUrl) ? null : serverUrl;
    }

    private async Task ConnectSignalRAsync(string serverUrl, CancellationToken cancellationToken)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl}/hubs/data", options =>
            {
                options.AccessTokenProvider = () => _authService.GetAccessTokenAsync();
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        _hubConnection.Reconnecting += _ => { SetConnected(false); return Task.CompletedTask; };
        _hubConnection.Reconnected += _ => { SetConnected(true); OnReconnected?.Invoke(); return ReauthorizeAsync(); };
        _hubConnection.Closed += _ => { SetConnected(false); return Task.CompletedTask; };

        _hubConnection.On<JsonElement>("dataUpdate", HandleDataUpdate);
        _hubConnection.On<JsonElement>("create", HandleStorageCreate);

        try
        {
            await _hubConnection.StartAsync(cancellationToken);
            SetConnected(true);
            await AuthorizeHubAsync();
            await SubscribeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to SignalR hub at {ServerUrl}", serverUrl);
            SetConnected(false);
        }

        ConnectAlarmHub(serverUrl, cancellationToken);
    }

    private void ConnectAlarmHub(string serverUrl, CancellationToken cancellationToken)
    {
        _alarmHubConnection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl}/hubs/alarms", options =>
            {
                options.AccessTokenProvider = () => _authService.GetAccessTokenAsync();
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        var connection = _alarmHubConnection;
        connection.Reconnected += _ => SubscribeAlarmsAsync(connection);
        foreach (var (eventName, kind) in AlarmHubEvents.Kinds)
        {
            var level = kind.Cleared ? AlarmLevel.Clear : kind.Urgent ? AlarmLevel.Urgent : AlarmLevel.Alarm;
            connection.On<JsonElement>(eventName, data => OnAlarm?.Invoke(new AlarmEventArgs(level, data)));
        }

        _ = StartAlarmHubAsync(connection, serverUrl, cancellationToken);
    }

    /// <summary>
    /// Starts the alarm hub, retrying until it connects or the connection is closed: automatic
    /// reconnect covers only a connection that was once up, and alarms have no polling fallback.
    /// </summary>
    private async Task StartAlarmHubAsync(HubConnection connection, string serverUrl, CancellationToken cancellationToken)
    {
        var policy = new RetryPolicy();
        for (var attempt = 0; !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await connection.StartAsync(cancellationToken);
                await SubscribeAlarmsAsync(connection);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                _logger.LogWarning(ex, "Failed to connect to the alarm hub at {ServerUrl}; retrying", serverUrl);
            }

            try
            {
                await Task.Delay(policy.Delay(attempt), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Joins the tenant's alarm-subscribers group, which authenticates in-band. The server replays
    /// the alarm that is sounding, so this runs once per connection, not on a token refresh.
    /// </summary>
    private async Task SubscribeAlarmsAsync(HubConnection connection)
    {
        try
        {
            if (connection.State != HubConnectionState.Connected) return;

            var token = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return;

            var subscribed = await connection.InvokeAsync<JsonElement>("Subscribe", new { jwtToken = token });
            if (!subscribed.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                _logger.LogWarning("Alarm hub rejected the subscription; alarms will not be raised");
            }
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            _logger.LogWarning(ex, "Failed to subscribe on the alarm hub; alarms will not be raised");
        }
    }

    private static bool IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException or Microsoft.AspNetCore.SignalR.HubException or InvalidOperationException
            or System.Net.WebSockets.WebSocketException or TimeoutException or IOException;

    private async Task AuthorizeHubAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected) return;

        var token = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        await _hubConnection.InvokeAsync<object>("Authorize", new Dictionary<string, object?>
        {
            ["client"] = "Nocturne.Desktop.Tray",
            ["token"] = token,
        });
    }

    private async Task SubscribeAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected) return;

        await _hubConnection.InvokeAsync<object>("Subscribe", new
        {
            collections = new[] { "entries", "devicestatus" },
        });
    }

    private async Task ReauthorizeAsync()
    {
        await AuthorizeHubAsync();
        await SubscribeAsync();
    }

    private async void OnAuthStateChanged()
    {
        try
        {
            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                await AuthorizeHubAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update auth state on SignalR hub");
        }
    }

    private void HandleDataUpdate(JsonElement data)
    {
        if (data.TryGetProperty("sgvs", out var sgvs) && sgvs.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sgvs.EnumerateArray())
            {
                var reading = ParseSignalRReading(entry);
                if (reading is not null) OnGlucoseReading?.Invoke(reading);
            }
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                var reading = ParseSignalRReading(entry);
                if (reading is not null) OnGlucoseReading?.Invoke(reading);
            }
        }
    }

    private void HandleStorageCreate(JsonElement data)
    {
        var reading = ParseSignalRReading(data);
        if (reading is not null) OnGlucoseReading?.Invoke(reading);
    }

    private static V4GlucoseReading? ParseSignalRReading(JsonElement entry)
    {
        if (!entry.TryGetProperty("sgv", out var sgvProp)) return null;

        var direction = Direction.NONE;
        if (entry.TryGetProperty("direction", out var dirProp) && dirProp.ValueKind == JsonValueKind.String)
        {
            if (Enum.TryParse<Direction>(dirProp.GetString(), true, out var parsed))
                direction = parsed;
        }

        return new V4GlucoseReading
        {
            Sgv = sgvProp.GetDouble(),
            Direction = direction,
            TrendRate = entry.TryGetProperty("trendRate", out var rate) ? rate.GetDouble() : null,
            Delta = entry.TryGetProperty("delta", out var delta) ? delta.GetDouble() : null,
            Mills = entry.TryGetProperty("mills", out var mills) ? mills.GetInt64()
                  : entry.TryGetProperty("date", out var date) ? date.GetInt64() : 0,
        };
    }

    private void StartPolling(CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_settingsService.Settings.PollingIntervalSeconds),
                        token);

                    var (reading, _) = await FetchSummaryAsync(hours: 0, token);
                    if (reading is not null) OnGlucoseReading?.Invoke(reading);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Polling failure (non-fatal; SignalR is the primary channel)");
                }
            }
        }, token);
    }

    private void SetConnected(bool connected)
    {
        if (_isConnected != connected)
        {
            _isConnected = connected;
            OnConnectionChanged?.Invoke(connected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _authService.AuthStateChanged -= OnAuthStateChanged;
        await DisconnectAsync();
    }

    private sealed class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);

        public TimeSpan Delay(long attempt) => Delays[(int)Math.Min(attempt, Delays.Length - 1)];
    }
}

public enum AlarmLevel
{
    Alarm,
    Urgent,
    Clear,
}

public sealed record AlarmEventArgs(AlarmLevel Level, JsonElement Data);
