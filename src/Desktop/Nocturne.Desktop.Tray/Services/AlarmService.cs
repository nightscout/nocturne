using System.Text.Json;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

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

    private static void ShowAlarmToast(AlarmEventArgs args, bool isUrgent)
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

    private static void ClearAlarmToasts()
    {
        AppNotificationManager.Default.RemoveByGroupAsync("glucose-alarms");
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Toast was clicked -- no-op, the flyout shows current data if opened.
    }

    public void Dispose()
    {
        if (_initialized)
        {
            AppNotificationManager.Default.Unregister();
        }
    }
}
