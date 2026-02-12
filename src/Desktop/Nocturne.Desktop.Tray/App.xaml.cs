using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nocturne.Desktop.Tray.Services;
using Nocturne.Desktop.Tray.TrayIcon;
using Nocturne.Desktop.Tray.Views;
using Nocturne.Widget.Contracts;
using Nocturne.Widget.Infrastructure.Windows;

namespace Nocturne.Desktop.Tray;

/// <summary>
/// Application entry point. Manages single-instance behavior, OIDC auth,
/// service wiring, and the tray icon lifecycle.
///
/// Uses ICredentialStore from Widget.Contracts for secure token storage,
/// sharing the WindowsCredentialStore implementation with the Windows 11 widget.
/// </summary>
public partial class App : Application
{
    private SettingsService _settingsService = null!;
    private ICredentialStore _credentialStore = null!;
    private OidcAuthService _authService = null!;
    private NocturneClient _nocturneClient = null!;
    private GlucoseStateService _glucoseState = null!;
    private AlarmService _alarmService = null!;
    private TrayIconManager _trayIcon = null!;
    private FlyoutWindow _flyoutWindow = null!;

    private Window _hiddenWindow = null!;
    private CancellationTokenSource _appCts = new();

    public App()
    {
        this.InitializeComponent();
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nocturne",
        "tray-debug.log"
    );

    private static void Log(string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log("OnLaunched started");

            // Clean up legacy protocol handler registration from previous versions.
            try
            {
                ActivationRegistrationManager.UnregisterForProtocolActivation("nocturne-tray", "");
            }
            catch
            { /* Ignore if not registered */
            }

            // Ensure single instance
            var mainInstance = AppInstance.FindOrRegisterForKey("NocturneTray");
            if (!mainInstance.IsCurrent)
            {
                Log("Secondary instance, exiting");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                return;
            }

            // Initialize services
            _settingsService = new SettingsService();
            await _settingsService.LoadAsync();
            Log("Settings loaded");

            // Use shared WindowsCredentialStore with tray-specific target names
            _credentialStore = new WindowsCredentialStore(
                NullLogger<WindowsCredentialStore>.Instance,
                "Nocturne.Tray.OAuth",
                "Nocturne.Tray.DeviceAuth"
            );

            _authService = new OidcAuthService(_settingsService, _credentialStore);
            _glucoseState = new GlucoseStateService();
            _nocturneClient = new NocturneClient(
                _settingsService,
                _authService,
                NullLogger<NocturneClient>.Instance
            );
            _alarmService = new AlarmService(_settingsService);
            _alarmService.Initialize();
            Log("Services initialized");

            // Create a hidden window (required by WinUI 3 for the app to stay alive)
            _hiddenWindow = new Window { Title = "Nocturne Tray" };
            Log(
                $"Hidden window created, Content={_hiddenWindow.Content}, XamlRoot={_hiddenWindow.Content?.XamlRoot}"
            );

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_hiddenWindow);
            ShowWindow(hwnd, SW_HIDE);
            Log("Window hidden");

            // Set up tray icon
            _trayIcon = new TrayIconManager(_hiddenWindow.Content?.XamlRoot);
            Log("TrayIconManager created");
            _trayIcon.FlyoutRequested += OnFlyoutRequested;
            _trayIcon.SettingsRequested += OnSettingsRequested;
            _trayIcon.ExitRequested += OnExitRequested;
            _trayIcon.ForceCreate();
            await _trayIcon.SetConnectingAsync(_settingsService.Settings);
            Log("Tray icon ForceCreate done");

            // Create flyout window
            _flyoutWindow = new FlyoutWindow(_glucoseState, _settingsService.Settings);
            Log("FlyoutWindow created");

            // Wire up data events
            _nocturneClient.OnGlucoseReading += OnGlucoseReading;
            _nocturneClient.OnAlarm += OnAlarm;
            _nocturneClient.OnConnectionChanged += OnConnectionChanged;
            _nocturneClient.OnReconnected += OnReconnected;
            _glucoseState.StateChanged += OnStateChanged;

            // Wire auth state changes to connect/disconnect
            _authService.AuthStateChanged += OnAuthStateChanged;

            // If we have stored tokens, validate them and connect
            Log($"HasServerUrl={_settingsService.HasServerUrl}");
            if (_settingsService.HasServerUrl && await _credentialStore.HasCredentialsAsync())
            {
                Log("Has credentials, initializing auth");
                await _authService.InitializeAsync();
                if (_authService.IsAuthenticated)
                {
                    Log("Authenticated, connecting");
                    await ConnectAsync();
                }
                else
                {
                    Log("Not authenticated, showing settings");
                    ShowSettings();
                }
            }
            else
            {
                Log("No server URL or credentials, showing settings");
                ShowSettings();
            }
            Log("OnLaunched completed");
        }
        catch (Exception ex)
        {
            Log($"EXCEPTION in OnLaunched: {ex}");
        }
    }

    private async Task ConnectAsync()
    {
        await _trayIcon.SetConnectingAsync(_settingsService.Settings);

        await _nocturneClient.ConnectAsync(_appCts.Token);

        await BackfillChartDataAsync();
    }

    private async Task BackfillChartDataAsync()
    {
        var hours = _settingsService.Settings.ChartHours;

        // Try v4 summary first (provides current + history in a single call)
        var (current, history) = await _nocturneClient.FetchSummaryAsync(hours, _appCts.Token);

        if (history.Count > 0)
        {
            _glucoseState.SetHistory(history);
            return;
        }

        // Fallback: fetch from v1 entries endpoint which queries by time range directly
        var readings = await _nocturneClient.FetchRecentReadingsAsync(hours, _appCts.Token);
        if (readings.Count > 0)
        {
            _glucoseState.SetHistory(readings);
        }
        else if (current is not null)
        {
            _glucoseState.ProcessReading(current);
        }
    }

    private void OnGlucoseReading(Models.GlucoseReading reading)
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Normal,
            () =>
            {
                _glucoseState.ProcessReading(reading);
            }
        );
    }

    private void OnAlarm(Services.AlarmEventArgs args)
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.High,
            () =>
            {
                _alarmService.HandleAlarm(args);
            }
        );
    }

    private void OnReconnected()
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Normal,
            async () =>
            {
                try
                {
                    var hours = _settingsService.Settings.ChartHours;
                    var readings = await _nocturneClient.FetchRecentReadingsAsync(hours, _appCts.Token);
                    if (readings.Count > 0)
                    {
                        _glucoseState.MergeReadings(readings);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Backfill on reconnect failed: {ex.Message}");
                }
            }
        );
    }

    private void OnConnectionChanged(bool isConnected)
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Normal,
            () =>
            {
                _glucoseState.IsConnected = isConnected;
                _flyoutWindow.UpdateConnectionStatus(isConnected);
            }
        );
    }

    private void OnAuthStateChanged()
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Normal,
            async () =>
            {
                if (_authService.IsAuthenticated)
                {
                    if (!_nocturneClient.IsConnected)
                    {
                        await ConnectAsync();
                    }
                }
                else
                {
                    await _nocturneClient.DisconnectAsync();
                    await _trayIcon.SetConnectingAsync(_settingsService.Settings);
                }
            }
        );
    }

    private async void OnStateChanged()
    {
        await _trayIcon.UpdateAsync(_glucoseState.CurrentReading, _settingsService.Settings);
        _flyoutWindow.RefreshContent();
    }

    private void OnFlyoutRequested(object? sender, EventArgs e)
    {
        _flyoutWindow.Toggle();
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        ShowSettings();
    }

    private void ShowSettings()
    {
        var settingsWindow = new SettingsWindow(_settingsService, _authService);
        settingsWindow.SettingsSaved += async (_, _) =>
        {
            if (_authService.IsAuthenticated)
            {
                await _nocturneClient.DisconnectAsync();
                await ConnectAsync();
            }
        };
        settingsWindow.SignOutRequested += async (_, _) =>
        {
            await _nocturneClient.DisconnectAsync();
        };
        settingsWindow.BringToFront();
    }

    private async void OnExitRequested(object? sender, EventArgs e)
    {
        _appCts.Cancel();
        await _nocturneClient.DisposeAsync();
        _authService.Dispose();
        _alarmService.Dispose();
        _trayIcon.Dispose();
        _glucoseState.Dispose();
        _hiddenWindow.Close();
        Environment.Exit(0);
    }

    private const int SW_HIDE = 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
