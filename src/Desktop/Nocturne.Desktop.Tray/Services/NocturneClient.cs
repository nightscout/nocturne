using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Nocturne.Desktop.Tray.Models;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// HTTP + SignalR client for the Nocturne API.
/// Authenticates via Bearer tokens managed by OidcAuthService.
/// Real-time data flows through the SignalR DataHub with HTTP polling as fallback.
/// </summary>
public sealed class NocturneClient : IAsyncDisposable
{
    private readonly SettingsService _settingsService;
    private readonly OidcAuthService _authService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _pollCts;
    private bool _isConnected;

    public event Action<GlucoseReading>? OnGlucoseReading;
    public event Action<AlarmEventArgs>? OnAlarm;
    public event Action<bool>? OnConnectionChanged;

    public bool IsConnected => _isConnected;

    public NocturneClient(SettingsService settingsService, OidcAuthService authService, ILogger<NocturneClient> logger)
    {
        _settingsService = settingsService;
        _authService = authService;
        _logger = logger;
        _httpClient = new HttpClient();

        _authService.AuthStateChanged += OnAuthStateChanged;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return;
        if (!_authService.IsAuthenticated) return;

        await EnsureBearerTokenAsync();
        await ConnectSignalRAsync(serverUrl, cancellationToken);
        StartPolling(cancellationToken);
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

        SetConnected(false);
    }

    public async Task<GlucoseReading?> FetchCurrentReadingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var serverUrl = GetServerUrl();
            if (serverUrl is null) return null;

            await EnsureBearerTokenAsync();
            var response = await _httpClient.GetAsync($"{serverUrl}/api/v1/entries/current", cancellationToken);
            response.EnsureSuccessStatusCode();

            var entries = await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken);
            if (entries is null || entries.Length == 0) return null;

            return ParseEntry(entries[0]);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch current glucose reading");
            return null;
        }
    }

    public async Task<IReadOnlyList<GlucoseReading>> FetchRecentReadingsAsync(int hours = 3, CancellationToken cancellationToken = default)
    {
        try
        {
            var serverUrl = GetServerUrl();
            if (serverUrl is null) return [];

            await EnsureBearerTokenAsync();
            var since = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeMilliseconds();
            var url = $"{serverUrl}/api/v1/entries.json?find[mills][$gte]={since}&count=1000&sort$desc=mills";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var entries = await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken);
            if (entries is null) return [];

            return entries
                .Select(ParseEntry)
                .Where(r => r is not null)
                .Cast<GlucoseReading>()
                .OrderBy(r => r.Mills)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch recent glucose readings");
            return [];
        }
    }

    private string? GetServerUrl()
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        return string.IsNullOrEmpty(serverUrl) ? null : serverUrl;
    }

    private async Task EnsureBearerTokenAsync()
    {
        _httpClient.DefaultRequestHeaders.Authorization = null;
        var token = await _authService.GetAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
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
        _hubConnection.Reconnected += _ => { SetConnected(true); return ReauthorizeAsync(); };
        _hubConnection.Closed += _ => { SetConnected(false); return Task.CompletedTask; };

        _hubConnection.On<JsonElement>("dataUpdate", HandleDataUpdate);
        _hubConnection.On<JsonElement>("alarm", data =>
            OnAlarm?.Invoke(new AlarmEventArgs(AlarmLevel.Alarm, data)));
        _hubConnection.On<JsonElement>("urgent_alarm", data =>
            OnAlarm?.Invoke(new AlarmEventArgs(AlarmLevel.Urgent, data)));
        _hubConnection.On<JsonElement>("clear_alarm", data =>
            OnAlarm?.Invoke(new AlarmEventArgs(AlarmLevel.Clear, data)));
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
    }

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
            await EnsureBearerTokenAsync();

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
                var reading = ParseEntry(entry);
                if (reading is not null) OnGlucoseReading?.Invoke(reading);
            }
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                var reading = ParseEntry(entry);
                if (reading is not null) OnGlucoseReading?.Invoke(reading);
            }
        }
    }

    private void HandleStorageCreate(JsonElement data)
    {
        var reading = ParseEntry(data);
        if (reading is not null) OnGlucoseReading?.Invoke(reading);
    }

    private void StartPolling(CancellationToken cancellationToken)
    {
        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_settingsService.Settings.PollingIntervalSeconds),
                        token);

                    var reading = await FetchCurrentReadingAsync(token);
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

    private static GlucoseReading? ParseEntry(JsonElement entry)
    {
        if (!entry.TryGetProperty("sgv", out var sgvProp)) return null;

        var sgv = sgvProp.GetDouble();

        return new GlucoseReading
        {
            Sgv = sgv,
            Mgdl = entry.TryGetProperty("mgdl", out var mgdl) ? mgdl.GetDouble() : sgv,
            Mmol = entry.TryGetProperty("mmol", out var mmol) ? mmol.GetDouble() : null,
            Direction = entry.TryGetProperty("direction", out var dir) ? dir.GetString() : null,
            Trend = entry.TryGetProperty("trend", out var trend) ? trend.GetInt32() : null,
            TrendRate = entry.TryGetProperty("trendRate", out var rate) ? rate.GetDouble() : null,
            Delta = entry.TryGetProperty("delta", out var delta) ? delta.GetDouble() : null,
            Mills = entry.TryGetProperty("mills", out var mills) ? mills.GetInt64()
                  : entry.TryGetProperty("date", out var date) ? date.GetInt64() : 0,
            DateString = entry.TryGetProperty("dateString", out var ds) ? ds.GetString() : null,
            Device = entry.TryGetProperty("device", out var dev) ? dev.GetString() : null,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _authService.AuthStateChanged -= OnAuthStateChanged;
        await DisconnectAsync();
        _httpClient.Dispose();
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

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
            return Delays[index];
        }
    }
}

public enum AlarmLevel
{
    Alarm,
    Urgent,
    Clear,
}

public sealed record AlarmEventArgs(AlarmLevel Level, JsonElement Data);
