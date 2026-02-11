using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nocturne.Desktop.Tray.Services;
using Nocturne.Desktop.Tray.TrayIcon;
using Nocturne.Desktop.Tray.Views;

namespace Nocturne.Desktop.Tray;

/// <summary>
/// Application entry point. Manages single-instance behavior, OIDC auth,
/// service wiring, and the tray icon lifecycle.
/// </summary>
public partial class App : Application
{
    private SettingsService _settingsService = null!;
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

        // Handle redirected activations (protocol activation from OIDC callback)
        mainInstance.Activated += OnInstanceActivated;

        // Initialize services
        _settingsService = new SettingsService();
        await _settingsService.LoadAsync();

        _authService = new OidcAuthService(_settingsService);
        _glucoseState = new GlucoseStateService();
        _nocturneClient = new NocturneClient(_settingsService, _authService);
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

        // Wire auth state changes to connect/disconnect
        _authService.AuthStateChanged += OnAuthStateChanged;

        // If we have stored tokens, validate them and connect
        if (_settingsService.HasServerUrl && _settingsService.IsAuthenticated)
        {
            await _authService.InitializeAsync();
            if (_authService.IsAuthenticated)
            {
                await ConnectAsync();
            }
            else
            {
                // Stored tokens were invalid — prompt re-authentication
                ShowSettings();
            }
        }
        else
        {
            ShowSettings();
        }
    }

    private async Task ConnectAsync()
    {
        await _trayIcon.SetConnectingAsync(_settingsService.Settings);

        await _nocturneClient.ConnectAsync(_appCts.Token);

        var current = await _nocturneClient.FetchCurrentReadingAsync(_appCts.Token);
        if (current is not null)
        {
            _glucoseState.ProcessReading(current);
        }

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

    private void OnAuthStateChanged()
    {
        _hiddenWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            if (_authService.IsAuthenticated)
            {
                // Tokens received or refreshed — connect if not already connected
                if (!_nocturneClient.IsConnected)
                {
                    await ConnectAsync();
                }
            }
            else
            {
                // Signed out or tokens revoked — disconnect
                await _nocturneClient.DisconnectAsync();
                await _trayIcon.SetConnectingAsync(_settingsService.Settings);
            }
        });
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
            // Reconnect with updated settings (thresholds, chart hours, etc.)
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
        settingsWindow.Activate();
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

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Protocol
            && args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
        {
            _hiddenWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                HandleProtocolActivation(protocolArgs.Uri);
            });
        }
    }

    private async void HandleProtocolActivation(Uri uri)
    {
        // nocturne-tray://auth/callback?access_token=xxx&refresh_token=yyy&expires_in=900
        if (uri.AbsolutePath.TrimStart('/') == "auth/callback")
        {
            var success = await _authService.HandleCallbackAsync(uri);
            if (!success)
            {
                // Auth failed — show settings so the user can retry
                ShowSettings();
            }
        }
    }

    private const int SW_HIDE = 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
