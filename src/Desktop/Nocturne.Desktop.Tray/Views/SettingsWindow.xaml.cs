using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Desktop.Tray.Models;
using Nocturne.Desktop.Tray.Services;
using Nocturne.Widget.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace Nocturne.Desktop.Tray.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly OidcAuthService _authService;

    public event EventHandler? SettingsSaved;
    public event EventHandler? SignOutRequested;

    public SettingsWindow(SettingsService settingsService, OidcAuthService authService)
    {
        this.InitializeComponent();
        _settingsService = settingsService;
        _authService = authService;

        ResizeWindow(480, 560);

        _authService.AuthStateChanged += UpdateAuthStatus;
        this.Closed += (_, _) => _authService.AuthStateChanged -= UpdateAuthStatus;

        LoadSettings();
        UpdateAuthStatus();
    }

    private void LoadSettings()
    {
        var s = _settingsService.Settings;

        ServerUrlBox.Text = s.ServerUrl ?? "";

        UnitCombo.SelectedIndex = s.Unit == GlucoseUnit.MmolL ? 1 : 0;
        ChartHoursBox.Value = s.ChartHours;

        UrgentLowBox.Value = s.UrgentLowThreshold;
        LowBox.Value = s.LowThreshold;
        HighBox.Value = s.HighThreshold;
        UrgentHighBox.Value = s.UrgentHighThreshold;

        EnableAlarmsToggle.IsOn = s.EnableAlarmToasts;
        EnableUrgentAlarmsToggle.IsOn = s.EnableUrgentAlarmToasts;
    }

    private void UpdateAuthStatus()
    {
        if (_authService.IsAuthenticated)
        {
            AuthIndicator.Fill = new SolidColorBrush(GlucoseRangeHelper.InRangeColor);
            AuthStatusText.Text = "Signed in";
            SignInOutButton.Content = "Sign Out";
        }
        else
        {
            AuthIndicator.Fill = new SolidColorBrush(GlucoseRangeHelper.UrgentColor);
            AuthStatusText.Text = "Not signed in";
            SignInOutButton.Content = "Sign In";
        }
    }

    private async void OnSignInOutClick(object sender, RoutedEventArgs e)
    {
        if (_authService.IsAuthenticated)
        {
            await _authService.SignOutAsync();
            SignOutRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _settingsService.Settings.ServerUrl = ServerUrlBox.Text.Trim();
            await _settingsService.SaveAsync();

            if (!_settingsService.HasServerUrl)
            {
                AuthStatusText.Text = "Enter a server URL first";
                return;
            }

            _authService.StartLogin();
            AuthStatusText.Text = "Waiting for browser sign-in...";
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Settings;

        s.ServerUrl = ServerUrlBox.Text.Trim();
        s.Unit = UnitCombo.SelectedIndex == 1 ? GlucoseUnit.MmolL : GlucoseUnit.MgDl;
        s.ChartHours = (int)ChartHoursBox.Value;
        s.UrgentLowThreshold = (int)UrgentLowBox.Value;
        s.LowThreshold = (int)LowBox.Value;
        s.HighThreshold = (int)HighBox.Value;
        s.UrgentHighThreshold = (int)UrgentHighBox.Value;
        s.EnableAlarmToasts = EnableAlarmsToggle.IsOn;
        s.EnableUrgentAlarmToasts = EnableUrgentAlarmsToggle.IsOn;

        await _settingsService.SaveAsync();
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        this.Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void ResizeWindow(int width, int height)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(width, height));
    }

    /// <summary>
    /// Brings the window to the foreground, even if the app wasn't previously focused.
    /// </summary>
    public void BringToFront()
    {
        Activate();
        var hwnd = WindowNative.GetWindowHandle(this);
        SetForegroundWindow(hwnd);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);
}
