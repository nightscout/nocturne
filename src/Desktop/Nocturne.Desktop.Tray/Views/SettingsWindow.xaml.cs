using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Desktop.Tray.Models;
using Nocturne.Desktop.Tray.Services;
using Nocturne.Widget.Contracts;
using Nocturne.Widget.Contracts.Helpers;
using Windows.Graphics;
using WinRT.Interop;

namespace Nocturne.Desktop.Tray.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly OidcAuthService _authService;
    private readonly Dictionary<string, ToggleSwitch> _alarmToggles = [];
    private bool _isLoading;

    public event EventHandler? SettingsSaved;
    public event EventHandler? SignOutRequested;

    public SettingsWindow(SettingsService settingsService, OidcAuthService authService)
    {
        this.InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        _settingsService = settingsService;
        _authService = authService;

        ResizeWindow(500, 720);

        _authService.AuthStateChanged += UpdateAuthStatus;
        this.Closed += (_, _) => _authService.AuthStateChanged -= UpdateAuthStatus;

        LoadSettings();
        UpdateAuthStatus();

        // Wire SelectionChanged in code-behind; XAML-declared SelectionChanged
        // on ComboBox causes XamlParseException with WindowsAppSDK 1.8.
        UnitCombo.SelectionChanged += OnUnitChanged;
    }

    private void LoadSettings()
    {
        _isLoading = true;

        var s = _settingsService.Settings;

        ServerUrlBox.Text = s.ServerUrl ?? "";

        UnitCombo.SelectedIndex = s.Unit == GlucoseUnit.MmolL ? 1 : 0;
        ChartHoursBox.Value = s.ChartHours;

        var isMmol = s.Unit == GlucoseUnit.MmolL;
        ApplyThresholdRanges(isMmol);

        UrgentLowBox.Value = isMmol
            ? Round1(GlucoseFormatHelper.ToMmol(s.UrgentLowThreshold))
            : s.UrgentLowThreshold;
        LowBox.Value = isMmol ? Round1(GlucoseFormatHelper.ToMmol(s.LowThreshold)) : s.LowThreshold;
        HighBox.Value = isMmol
            ? Round1(GlucoseFormatHelper.ToMmol(s.HighThreshold))
            : s.HighThreshold;
        UrgentHighBox.Value = isMmol
            ? Round1(GlucoseFormatHelper.ToMmol(s.UrgentHighThreshold))
            : s.UrgentHighThreshold;

        BuildAlarmToggles(s);

        _isLoading = false;
    }

    private void OnUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
            return;

        var switchingToMmol = UnitCombo.SelectedIndex == 1;

        // Convert the currently displayed values
        if (switchingToMmol)
        {
            UrgentLowBox.Value = Round1(GlucoseFormatHelper.ToMmol(UrgentLowBox.Value));
            LowBox.Value = Round1(GlucoseFormatHelper.ToMmol(LowBox.Value));
            HighBox.Value = Round1(GlucoseFormatHelper.ToMmol(HighBox.Value));
            UrgentHighBox.Value = Round1(GlucoseFormatHelper.ToMmol(UrgentHighBox.Value));
        }
        else
        {
            UrgentLowBox.Value = Math.Round(GlucoseFormatHelper.ToMgdl(UrgentLowBox.Value));
            LowBox.Value = Math.Round(GlucoseFormatHelper.ToMgdl(LowBox.Value));
            HighBox.Value = Math.Round(GlucoseFormatHelper.ToMgdl(HighBox.Value));
            UrgentHighBox.Value = Math.Round(GlucoseFormatHelper.ToMgdl(UrgentHighBox.Value));
        }

        ApplyThresholdRanges(switchingToMmol);
    }

    private void ApplyThresholdRanges(bool isMmol)
    {
        if (isMmol)
        {
            ThresholdHeader.Text = "Glucose Thresholds (mmol/L)";
            SetNumberBoxRange(UrgentLowBox, 1.7, 5.6, 0.1);
            SetNumberBoxRange(LowBox, 2.2, 6.7, 0.1);
            SetNumberBoxRange(HighBox, 6.7, 16.7, 0.1);
            SetNumberBoxRange(UrgentHighBox, 8.3, 22.2, 0.1);
        }
        else
        {
            ThresholdHeader.Text = "Glucose Thresholds (mg/dL)";
            SetNumberBoxRange(UrgentLowBox, 30, 100, 1);
            SetNumberBoxRange(LowBox, 40, 120, 1);
            SetNumberBoxRange(HighBox, 120, 300, 1);
            SetNumberBoxRange(UrgentHighBox, 150, 400, 1);
        }
    }

    private static void SetNumberBoxRange(NumberBox box, double min, double max, double step)
    {
        box.Minimum = min;
        box.Maximum = max;
        box.SmallChange = step;
        box.LargeChange = step * 10;
    }

    private static double Round1(double value) => Math.Round(value, 1);

    private void BuildAlarmToggles(TraySettings settings)
    {
        AlarmTypesPanel.Children.Clear();
        _alarmToggles.Clear();

        foreach (var (type, label) in TraySettings.KnownAlarmTypes)
        {
            var toggle = new ToggleSwitch
            {
                Header = label,
                IsOn = !settings.DisabledAlarmTypes.Contains(type),
            };

            _alarmToggles[type] = toggle;
            AlarmTypesPanel.Children.Add(toggle);
        }
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
        var isMmol = UnitCombo.SelectedIndex == 1;

        s.ServerUrl = ServerUrlBox.Text.Trim();
        s.Unit = isMmol ? GlucoseUnit.MmolL : GlucoseUnit.MgDl;
        s.ChartHours = (int)ChartHoursBox.Value;

        // Thresholds are always stored in mg/dL; convert back if editing in mmol
        if (isMmol)
        {
            s.UrgentLowThreshold = (int)Math.Round(GlucoseFormatHelper.ToMgdl(UrgentLowBox.Value));
            s.LowThreshold = (int)Math.Round(GlucoseFormatHelper.ToMgdl(LowBox.Value));
            s.HighThreshold = (int)Math.Round(GlucoseFormatHelper.ToMgdl(HighBox.Value));
            s.UrgentHighThreshold = (int)
                Math.Round(GlucoseFormatHelper.ToMgdl(UrgentHighBox.Value));
        }
        else
        {
            s.UrgentLowThreshold = (int)UrgentLowBox.Value;
            s.LowThreshold = (int)LowBox.Value;
            s.HighThreshold = (int)HighBox.Value;
            s.UrgentHighThreshold = (int)UrgentHighBox.Value;
        }

        s.DisabledAlarmTypes = _alarmToggles
            .Where(kv => !kv.Value.IsOn)
            .Select(kv => kv.Key)
            .ToHashSet();

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
