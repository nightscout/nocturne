using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nocturne.Desktop.Tray.Models;
using Nocturne.Desktop.Tray.Services;
using Windows.UI;

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
            AuthIndicator.Fill = new SolidColorBrush(Color.FromArgb(255, 60, 180, 75));
            AuthStatusText.Text = "Signed in";
            SignInOutButton.Content = "Sign Out";
        }
        else
        {
            AuthIndicator.Fill = new SolidColorBrush(Color.FromArgb(255, 200, 30, 30));
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
}
