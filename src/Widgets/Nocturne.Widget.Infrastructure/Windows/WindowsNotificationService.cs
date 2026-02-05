using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Infrastructure.Windows;

/// <summary>
/// Windows toast notification implementation for system notifications
/// </summary>
public class WindowsNotificationService : ISystemNotificationService
{
    private readonly ILogger<WindowsNotificationService> _logger;

    /// <summary>
    /// Initializes a new instance of the WindowsNotificationService
    /// </summary>
    /// <param name="logger">The logger instance</param>
    public WindowsNotificationService(ILogger<WindowsNotificationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ShowAlarmNotificationAsync(string title, string message, int level, bool urgent)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .AddAttributionText($"Nocturne - Level {level} Alarm");

            // Set audio based on urgency
            if (urgent)
            {
                builder.AddAudio(
                    new ToastAudio
                    {
                        Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm"),
                        Loop = true,
                    }
                );

                // Add scenario for urgent alarms to ensure they're shown prominently
                builder.SetToastScenario(ToastScenario.Alarm);
            }
            else if (level >= 3)
            {
                builder.AddAudio(
                    new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm2") }
                );
            }

            // Add actions for alarm notifications
            builder.AddButton(
                new ToastButton()
                    .SetContent("Acknowledge")
                    .AddArgument("action", "acknowledge")
                    .AddArgument("alarmLevel", level.ToString())
            );

            builder.AddButton(
                new ToastButton()
                    .SetContent("Snooze")
                    .AddArgument("action", "snooze")
                    .AddArgument("alarmLevel", level.ToString())
            );

            builder.Show();
            _logger.LogInformation(
                "Alarm notification shown: {Title}, Level: {Level}, Urgent: {Urgent}",
                title,
                level,
                urgent
            );

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show alarm notification");
            throw;
        }
    }

    /// <inheritdoc />
    public Task ShowTrackerAlertAsync(
        string trackerName,
        string message,
        NotificationUrgency urgency
    )
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText($"Tracker Alert: {trackerName}")
                .AddText(message)
                .AddAttributionText("Nocturne Tracker");

            // Configure notification based on urgency
            switch (urgency)
            {
                case NotificationUrgency.Urgent:
                    builder.SetToastScenario(ToastScenario.Alarm);
                    builder.AddAudio(
                        new ToastAudio
                        {
                            Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm"),
                            Loop = true,
                        }
                    );
                    break;

                case NotificationUrgency.Hazard:
                    builder.AddAudio(
                        new ToastAudio
                        {
                            Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm2"),
                        }
                    );
                    break;

                case NotificationUrgency.Warn:
                    builder.AddAudio(
                        new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Default") }
                    );
                    break;

                case NotificationUrgency.Info:
                default:
                    // Default notification sound
                    break;
            }

            // Add action to open tracker details
            builder.AddButton(
                new ToastButton()
                    .SetContent("View Details")
                    .AddArgument("action", "viewTracker")
                    .AddArgument("trackerName", trackerName)
            );

            builder.Show();
            _logger.LogInformation(
                "Tracker alert shown: {TrackerName}, Urgency: {Urgency}",
                trackerName,
                urgency
            );

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show tracker alert notification");
            throw;
        }
    }

    /// <inheritdoc />
    public Task ClearNotificationsAsync()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
            _logger.LogInformation("All notifications cleared");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear notifications");
            throw;
        }
    }
}
