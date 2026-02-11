using Microsoft.UI.Xaml;
using Nocturne.Desktop.Tray.Models;
using Nocturne.Desktop.Tray.Services;

namespace Nocturne.Desktop.Tray.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;

    public event EventHandler? SettingsSaved;

    public SettingsWindow(SettingsService settingsService)
    {
        this.InitializeComponent();
        _settingsService = settingsService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settingsService.Settings;

        ServerUrlBox.Text = s.ServerUrl ?? "";
        ApiSecretBox.Password = _settingsService.GetApiSecret() ?? "";

        UnitCombo.SelectedIndex = s.Unit == GlucoseUnit.MmolL ? 1 : 0;
        ChartHoursBox.Value = s.ChartHours;

        UrgentLowBox.Value = s.UrgentLowThreshold;
        LowBox.Value = s.LowThreshold;
        HighBox.Value = s.HighThreshold;
        UrgentHighBox.Value = s.UrgentHighThreshold;

        EnableAlarmsToggle.IsOn = s.EnableAlarmToasts;
        EnableUrgentAlarmsToggle.IsOn = s.EnableUrgentAlarmToasts;
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

        // Store secret securely
        var secret = ApiSecretBox.Password;
        if (!string.IsNullOrEmpty(secret))
        {
            _settingsService.SetApiSecret(secret);
        }

        await _settingsService.SaveAsync();
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        this.Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
