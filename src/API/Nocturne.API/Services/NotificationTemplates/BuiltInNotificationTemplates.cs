using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.Notifications.External;
using Nocturne.API.Services.Platform.InstanceKey;
using Nocturne.API.Services.Platform.Releases;
using Nocturne.API.Services.Timezones;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.API.Services.Uploads;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.NotificationTemplates;

/// <summary>
/// Extension class that registers all built-in <see cref="NotificationTemplate"/> instances into a
/// <see cref="NotificationTemplateRegistry"/> at startup. Built-in types include tracker alerts,
/// meal-match suggestions, migration prompts, and admin/system notifications.
/// </summary>
public static class BuiltInNotificationTemplates
{
    public static NotificationTemplateRegistry AddBuiltInTemplates(
        this NotificationTemplateRegistry registry)
    {
        registry.Register(new NotificationTemplate
        {
            Type = "tracker.unconfigured",
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "settings-2",
            Source = "tracker-service"
        });

        registry.Register(new NotificationTemplate
        {
            Type = "tracker.alert",
            Category = NotificationCategory.Alert,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Warn,
            Icon = "timer",
            Source = "tracker-service"
        });

        registry.Register(new NotificationTemplate
        {
            Type = "system.statistics_summary",
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "info",
            Source = "system"
        });

        registry.Register(new NotificationTemplate
        {
            Type = "system.help_response",
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "help-circle",
            Source = "system"
        });

        registry.Register(new NotificationTemplate
        {
            Type = NotificationSubjectNaming.AnonymousLoginRequest,
            Category = NotificationCategory.ActionRequired,
            NamesSubject = true,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "user",
            Source = "auth"
        });

        registry.Register(new NotificationTemplate
        {
            Type = "glucose.predicted_low",
            Category = NotificationCategory.Alert,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Urgent,
            Icon = "trending-down",
            Source = "glucose-engine"
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.MealMatchingSuggestedMatch.ToWireString(),
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "utensils",
            Source = "meal-matching"
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.GlucoseCompressionLowReview.ToWireString(),
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "info",
            Source = "compression-low-engine"
        });

        registry.Register(new NotificationTemplate
        {
            Type = NotificationSubjectNaming.MembershipRequested,
            Category = NotificationCategory.ActionRequired,
            NamesSubject = true,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "user-plus",
            Source = "membership"
        });

        registry.Register(new NotificationTemplate
        {
            Type = MembershipRequestService.ApprovedNotificationType,
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "check-circle",
            Source = "membership"
        });

        registry.Register(new NotificationTemplate
        {
            Type = ShareLinkRotatedNotifier.NotificationType,
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "link",
            Source = "sharing"
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.TeamRenamed.ToWireString(),
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "users",
            Source = TeamRenameAnnouncer.Source
        });

        foreach (var (type, source) in new[]
                 {
                     (InAppNotificationType.TeamStaffWaiting.ToWireString(), TeamStaffAnnouncer.WaitingSource),
                     (InAppNotificationType.TeamStaffJoined.ToWireString(), TeamStaffAnnouncer.JoinedSource),
                 })
        {
            registry.Register(new NotificationTemplate
            {
                Type = type,
                Category = NotificationCategory.Informational,
                NamesSubject = false,
                DefaultUrgency = NotificationUrgency.Info,
                Icon = "users",
                Source = source
            });
        }

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.ProfileRawMmolValues.ToWireString(),
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Warn,
            Icon = "alert-triangle",
            Source = "profile"
        });

        registry.Register(new NotificationTemplate
        {
            Type = MembershipRequestService.DeniedNotificationType,
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "x-circle",
            Source = "membership"
        });

        foreach (var type in new[] { InAppNotificationType.ExternalOperator.ToWireString(), InAppNotificationType.ExternalApp.ToWireString() })
        {
            registry.Register(new NotificationTemplate
            {
                Type = type,
                Category = NotificationCategory.Informational,
                NamesSubject = false,
                DefaultUrgency = NotificationUrgency.Info,
                Icon = ExternalNotificationService.Icon,
            });
        }

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.SystemUpdateAvailable.ToWireString(),
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "refresh-cw",
            Source = ReleaseNoticeJob.Source,
            DefaultActions = [ReleaseNotesAction()]
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.SystemReleaseInstalled.ToWireString(),
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "gift",
            Source = ReleaseNoticeJob.Source,
            DefaultActions = [ReleaseNotesAction()]
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.SystemInstanceKeyChanged.ToWireString(),
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Warn,
            Icon = "key-round",
            Source = InstanceKeyChangeService.Source
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.SystemInstanceKeyChangedPlatform.ToWireString(),
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Warn,
            Icon = "key-round",
            Source = InstanceKeyChangeService.Source,
            DefaultActions =
            [
                new NotificationActionDto
                {
                    ActionId = InstanceKeyChangeService.OpenPlatformSettingsActionId,
                    Label = NotificationLabel.OpenPlatformSettings.ToWireString(),
                    Icon = "key-round",
                    Variant = "default"
                }
            ]
        });

        registry.Register(new NotificationTemplate
        {
            Type = ApiKeyHandler.RotationNudgeType,
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            Icon = "key"
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.AlertChannelNotSending.ToWireString(),
            Category = NotificationCategory.Alert,
            NamesSubject = true
        });

        registry.Register(new NotificationTemplate
        {
            Type = ClockAnomalyNotice.NotificationType,
            Category = NotificationCategory.Alert,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.AlertFiring.ToWireString(),
            Category = NotificationCategory.Alert,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.AlertUnevaluable.ToWireString(),
            Category = NotificationCategory.Alert,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.AlertHeld.ToWireString(),
            Category = NotificationCategory.Alert,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = DeviceClockService.SegmentNotificationType,
            Category = NotificationCategory.Informational,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = DeviceClockService.ZoneChangeNotificationType,
            Category = NotificationCategory.Informational,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppNotificationType.DataRejectedUploads.ToWireString(),
            Category = NotificationCategory.Informational,
            NamesSubject = false
        });

        return registry;
    }

    private static NotificationActionDto ReleaseNotesAction() => new()
    {
        ActionId = ReleaseNoticeJob.ReleaseNotesActionId,
        Label = NotificationLabel.ReleaseNotes.ToWireString(),
        Icon = "info",
        Variant = "default"
    };
}
