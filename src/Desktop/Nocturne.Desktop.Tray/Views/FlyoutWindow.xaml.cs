using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nocturne.Desktop.Tray.Models;
using Nocturne.Desktop.Tray.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Nocturne.Desktop.Tray.Views;

/// <summary>
/// Borderless flyout window positioned near the system tray area.
/// Uses Mica backdrop for a native Windows 11 appearance.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private readonly GlucoseStateService _glucoseState;
    private readonly TraySettings _settings;
    private bool _isVisible;

    public FlyoutWindow(GlucoseStateService glucoseState, TraySettings settings)
    {
        this.InitializeComponent();

        _glucoseState = glucoseState;
        _settings = settings;

        ConfigureWindow();
        ApplyBackdrop();
    }

    private void ConfigureWindow()
    {
        var presenter = GetAppWindowPresenter();
        if (presenter is OverlappedPresenter overlapped)
        {
            overlapped.IsResizable = false;
            overlapped.IsMaximizable = false;
            overlapped.IsMinimizable = false;
            overlapped.SetBorderAndTitleBar(false, false);
            overlapped.IsAlwaysOnTop = true;
        }

        // Start hidden
        var appWindow = GetAppWindow();
        appWindow.IsShownInSwitchers = false;
        appWindow.Hide();
    }

    private void ApplyBackdrop()
    {
        // Apply Mica backdrop for native Windows 11 look
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            this.SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            // Fallback to DesktopAcrylic on older systems
            this.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }

    /// <summary>
    /// Toggles the flyout visibility, positioning it near the taskbar.
    /// </summary>
    public void Toggle()
    {
        if (_isVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public new void Show()
    {
        RefreshContent();
        PositionNearTaskbar();

        var appWindow = GetAppWindow();
        appWindow.Show();
        _isVisible = true;

        // Auto-hide when clicking away
        this.Activated += OnDeactivated;
    }

    public new void Hide()
    {
        var appWindow = GetAppWindow();
        appWindow.Hide();
        _isVisible = false;
        this.Activated -= OnDeactivated;
    }

    /// <summary>
    /// Refreshes the flyout content with the latest glucose data.
    /// </summary>
    public void RefreshContent()
    {
        GlucoseCardControl.Update(_glucoseState.CurrentReading, _settings);

        var chartReadings = _glucoseState.GetReadingsForChart(_settings.ChartHours);
        MiniChartControl.Update(chartReadings, _settings);

        UpdateConnectionStatus(_glucoseState.IsConnected);

        DeviceText.Text = _glucoseState.CurrentReading?.Device ?? "";
    }

    public void UpdateConnectionStatus(bool isConnected)
    {
        ConnectionIndicator.Fill = isConnected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 180, 75))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 30, 30));
        ConnectionText.Text = isConnected ? "Connected" : "Disconnected";
    }

    private void PositionNearTaskbar()
    {
        var appWindow = GetAppWindow();
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;

        // Position at the bottom-right of the work area (above the taskbar)
        var windowWidth = 360;
        var windowHeight = 320;
        var margin = 12;

        var x = workArea.X + workArea.Width - windowWidth - margin;
        var y = workArea.Y + workArea.Height - windowHeight - margin;

        appWindow.MoveAndResize(new RectInt32(x, y, windowWidth, windowHeight));
    }

    private void OnDeactivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Hide();
        }
    }

    private AppWindow GetAppWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private AppWindowPresenter GetAppWindowPresenter()
    {
        return GetAppWindow().Presenter;
    }
}
