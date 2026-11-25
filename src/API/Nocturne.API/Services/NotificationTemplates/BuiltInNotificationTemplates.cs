using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Notifications;
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
            Type = IMealMatchingService.SuggestedMatchNotificationType,
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "utensils",
            Source = "meal-matching"
        });

        registry.Register(new NotificationTemplate
        {
            Type = CompressionLowDetectionService.NotificationType,
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
            Type = TeamRenameAnnouncer.NotificationType,
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "users",
            Source = TeamRenameAnnouncer.Source
        });

        foreach (var (type, source) in new[]
                 {
                     (TeamStaffAnnouncer.WaitingType, TeamStaffAnnouncer.WaitingSource),
                     (TeamStaffAnnouncer.JoinedType, TeamStaffAnnouncer.JoinedSource),
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
            Type = RawMmolProfileNotifier.NotificationType,
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

        registry.Register(new NotificationTemplate
        {
            Type = ReleaseNoticeJob.UpdateAvailableType,
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "refresh-cw",
            Source = ReleaseNoticeJob.Source,
            DefaultActions = [ReleaseNotesAction()]
        });

        registry.Register(new NotificationTemplate
        {
            Type = ReleaseNoticeJob.ReleaseInstalledType,
            Category = NotificationCategory.Informational,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Info,
            Icon = "gift",
            Source = ReleaseNoticeJob.Source,
            DefaultActions = [ReleaseNotesAction()]
        });

        registry.Register(new NotificationTemplate
        {
            Type = InstanceKeyChangeService.TenantNoticeType,
            Category = NotificationCategory.ActionRequired,
            NamesSubject = false,
            DefaultUrgency = NotificationUrgency.Warn,
            Icon = "key-round",
            Source = InstanceKeyChangeService.Source
        });

        registry.Register(new NotificationTemplate
        {
            Type = InstanceKeyChangeService.PlatformNoticeType,
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
                    Label = InstanceKeyChangeService.OpenPlatformSettingsActionId,
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
            Type = NotificationSubjectNaming.AlertChannelNotSending,
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
            Type = InAppProvider.NotificationType,
            Category = NotificationCategory.Alert,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppProvider.UnevaluableNotificationType,
            Category = NotificationCategory.Alert,
            NamesSubject = false
        });

        registry.Register(new NotificationTemplate
        {
            Type = InAppProvider.HeldNotificationType,
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
            Type = RejectedUploadRecorder.NotificationType,
            Category = NotificationCategory.Informational,
            NamesSubject = false
        });

        return registry;
    }

    private static NotificationActionDto ReleaseNotesAction() => new()
    {
        ActionId = ReleaseNoticeJob.ReleaseNotesActionId,
        Label = "release_notes",
        Icon = "info",
        Variant = "default"
    };
}
