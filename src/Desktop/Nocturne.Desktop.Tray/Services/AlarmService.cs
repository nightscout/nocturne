using System.Text.Json;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Desktop.Tray.Models;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Handles glucose alarms by showing Windows toast notifications.
/// Uses Windows App SDK AppNotification API for native toast support.
/// </summary>
public sealed class AlarmService : IDisposable
{
    private readonly SettingsService _settingsService;
    private bool _initialized;

    public AlarmService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Initialize()
    {
        if (_initialized) return;

        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnNotificationInvoked;
        manager.Register();
        _initialized = true;
    }

    /// <summary>
    /// Handles an alarm event from the SignalR connection.
    /// </summary>
    public void HandleAlarm(AlarmEventArgs args)
    {
        var settings = _settingsService.Settings;

        switch (args.Level)
        {
            case AlarmLevel.Urgent when settings.EnableUrgentAlarmToasts:
                ShowAlarmToast(args, isUrgent: true);
                break;
            case AlarmLevel.Alarm when settings.EnableAlarmToasts:
                ShowAlarmToast(args, isUrgent: false);
                break;
            case AlarmLevel.Clear:
                ClearAlarmToasts();
                break;
        }
    }

    /// <summary>
    /// Shows a toast notification for a glucose alarm based on the current reading state.
    /// </summary>
    public void ShowReadingAlarmIfNeeded(GlucoseReading reading)
    {
        var settings = _settingsService.Settings;
        var mgdl = reading.Mgdl;

        if (mgdl <= settings.UrgentLowThreshold && settings.EnableUrgentAlarmToasts)
        {
            ShowGlucoseToast(reading, "Urgent Low", isUrgent: true);
        }
        else if (mgdl >= settings.UrgentHighThreshold && settings.EnableUrgentAlarmToasts)
        {
            ShowGlucoseToast(reading, "Urgent High", isUrgent: true);
        }
        else if (mgdl <= settings.LowThreshold && settings.EnableAlarmToasts)
        {
            ShowGlucoseToast(reading, "Low", isUrgent: false);
        }
        else if (mgdl >= settings.HighThreshold && settings.EnableAlarmToasts)
        {
            ShowGlucoseToast(reading, "High", isUrgent: false);
        }
    }

    private void ShowAlarmToast(AlarmEventArgs args, bool isUrgent)
    {
        var title = "Glucose Alarm";
        var message = "Check your glucose level";

        if (args.Data.TryGetProperty("title", out var titleProp))
        {
            title = titleProp.GetString() ?? title;
        }
        if (args.Data.TryGetProperty("message", out var msgProp))
        {
            message = msgProp.GetString() ?? message;
        }

        var builder = new AppNotificationBuilder()
            .AddText(title)
            .AddText(message)
            .SetTag("nocturne-alarm")
            .SetGroup("glucose-alarms");

        if (isUrgent)
        {
            builder.SetScenario(AppNotificationScenario.Urgent);
        }

        var notification = builder.BuildNotification();
        AppNotificationManager.Default.Show(notification);
    }

    private void ShowGlucoseToast(GlucoseReading reading, string level, bool isUrgent)
    {
        var settings = _settingsService.Settings;
        var value = GlucoseRangeHelper.FormatValue(reading.Mgdl, settings.Unit);
        var unit = settings.Unit == GlucoseUnit.MmolL ? "mmol/L" : "mg/dL";
        var arrow = TrendHelper.GetArrowText(reading.Direction);
        var delta = GlucoseRangeHelper.FormatDelta(reading.Delta, settings.Unit);

        var builder = new AppNotificationBuilder()
            .AddText($"{level}: {value} {unit} {arrow}")
            .AddText($"Delta: {delta} {unit}")
            .SetTag("nocturne-alarm")
            .SetGroup("glucose-alarms");

        if (isUrgent)
        {
            builder.SetScenario(AppNotificationScenario.Urgent);
        }

        var notification = builder.BuildNotification();
        AppNotificationManager.Default.Show(notification);
    }

    private static void ClearAlarmToasts()
    {
        AppNotificationManager.Default.RemoveByGroupAsync("glucose-alarms");
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Toast was clicked — the app is already running in the tray,
        // so this is a no-op. The flyout will show current data if opened.
    }

    public void Dispose()
    {
        if (_initialized)
        {
            AppNotificationManager.Default.Unregister();
        }
    }
}
