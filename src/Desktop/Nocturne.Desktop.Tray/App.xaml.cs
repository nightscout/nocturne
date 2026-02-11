using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nocturne.Desktop.Tray.Services;
using Nocturne.Desktop.Tray.TrayIcon;
using Nocturne.Desktop.Tray.Views;

namespace Nocturne.Desktop.Tray;

/// <summary>
/// Application entry point. Manages single-instance behavior, service wiring,
/// and the tray icon lifecycle.
/// </summary>
public partial class App : Application
{
    private SettingsService _settingsService = null!;
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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Ensure single instance
        var mainInstance = AppInstance.FindOrRegisterForKey("NocturneTray");
        if (!mainInstance.IsCurrent)
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            await mainInstance.RedirectActivationToAsync(activationArgs);
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return;
        }

        // Handle redirected activations (e.g., protocol activation from OAuth callback)
        mainInstance.Activated += OnInstanceActivated;

        // Initialize services
        _settingsService = new SettingsService();
        await _settingsService.LoadAsync();

        _glucoseState = new GlucoseStateService();
        _nocturneClient = new NocturneClient(_settingsService);
        _alarmService = new AlarmService(_settingsService);
        _alarmService.Initialize();

        // Create a hidden window (required by WinUI 3 for the app to stay alive)
        _hiddenWindow = new Window
        {
            Title = "Nocturne Tray",
        };

        // Immediately hide the window; we only live in the tray
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_hiddenWindow);
        ShowWindow(hwnd, SW_HIDE);

        // Set up tray icon
        _trayIcon = new TrayIconManager(_hiddenWindow.Content.XamlRoot!);
        _trayIcon.FlyoutRequested += OnFlyoutRequested;
        _trayIcon.SettingsRequested += OnSettingsRequested;
        _trayIcon.ExitRequested += OnExitRequested;
        _trayIcon.ForceCreate();

        // Create flyout window
        _flyoutWindow = new FlyoutWindow(_glucoseState, _settingsService.Settings);

        // Wire up data events
        _nocturneClient.OnGlucoseReading += OnGlucoseReading;
        _nocturneClient.OnAlarm += OnAlarm;
        _nocturneClient.OnConnectionChanged += OnConnectionChanged;
        _glucoseState.StateChanged += OnStateChanged;

        // Show settings on first run, otherwise connect
        if (!_settingsService.IsConfigured)
        {
            ShowSettings();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        await _trayIcon.SetConnectingAsync(_settingsService.Settings);

        // Connect to the server
        await _nocturneClient.ConnectAsync(_appCts.Token);

        // Fetch initial data
        var current = await _nocturneClient.FetchCurrentReadingAsync(_appCts.Token);
        if (current is not null)
        {
            _glucoseState.ProcessReading(current);
        }

        // Load recent history for the chart
        var history = await _nocturneClient.FetchRecentReadingsAsync(
            _settingsService.Settings.ChartHours, _appCts.Token);
        if (history.Count > 0)
        {
            _glucoseState.SetHistory(history);
        }
    }

    private void OnGlucoseReading(Models.GlucoseReading reading)
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _glucoseState.ProcessReading(reading);
        });
    }

    private void OnAlarm(AlarmEventArgs args)
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            _alarmService.HandleAlarm(args);
        });
    }

    private void OnConnectionChanged(bool isConnected)
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _glucoseState.IsConnected = isConnected;
            _flyoutWindow.UpdateConnectionStatus(isConnected);
        });
    }

    private async void OnStateChanged()
    {
        // Update tray icon with latest reading
        await _trayIcon.UpdateAsync(_glucoseState.CurrentReading, _settingsService.Settings);

        // Refresh flyout if visible
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
        var settingsWindow = new SettingsWindow(_settingsService);
        settingsWindow.SettingsSaved += async (_, _) =>
        {
            // Reconnect with new settings
            await _nocturneClient.DisconnectAsync();
            await ConnectAsync();
        };
        settingsWindow.Activate();
    }

    private async void OnExitRequested(object? sender, EventArgs e)
    {
        _appCts.Cancel();
        await _nocturneClient.DisposeAsync();
        _alarmService.Dispose();
        _trayIcon.Dispose();
        _glucoseState.Dispose();
        _hiddenWindow.Close();
        Environment.Exit(0);
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        // Handle protocol activation from OAuth callback
        if (args.Kind == ExtendedActivationKind.Protocol
            && args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
        {
            _hiddenWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                HandleProtocolActivation(protocolArgs.Uri);
            });
        }
    }

    private void HandleProtocolActivation(Uri uri)
    {
        // nocturne-tray://auth/callback?token=xxx&refresh=yyy
        if (uri.AbsolutePath.TrimStart('/') == "auth/callback")
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var token = query["token"];
            var refresh = query["refresh"];

            if (!string.IsNullOrEmpty(token))
            {
                _settingsService.SetAccessToken(token);
            }
            if (!string.IsNullOrEmpty(refresh))
            {
                _settingsService.SetRefreshToken(refresh);
            }

            // Reconnect with new credentials
            _ = Task.Run(async () =>
            {
                await _nocturneClient.DisconnectAsync();
                await ConnectAsync();
            });
        }
    }

    private const int SW_HIDE = 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
