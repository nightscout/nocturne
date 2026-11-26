using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Notifications.External;

/// <summary>
/// Puts notices from senders outside Nocturne in the notification trays of a tenant's members, and
/// withdraws them. The operator and connected apps both send through here.
/// </summary>
/// <remarks>
/// A notice is always <see cref="NotificationUrgency.Info"/> and
/// <see cref="NotificationCategory.Informational"/>, so <c>DeviceNotificationMirror.Qualifies</c>
/// never mirrors it to a device: it stays in the tray, with no banner and no push. Its only actions
/// are <c>navigate</c>, when it has a link, and <c>dismiss</c>, which the notification service runs
/// as built-in archives because no handler is registered for its type.
/// <para>
/// Each member's notice is claimed in <see cref="IExternalNotificationLedger"/> before it is created
/// and the claim is released when creating it fails, so a key reaches a member once.
/// </para>
/// <para>
/// The notices are written in a <see cref="TenantJobScope"/> for the tenant, which the operator's
/// request has none of; it reads as no one and returns only the notices it wrote.
/// </para>
/// </remarks>
public interface IExternalNotificationService
{
    /// <param name="sender">Who sends it.</param>
    /// <param name="tenant">The tenant whose members receive it.</param>
    /// <param name="request">The notice.</param>
    /// <param name="grantingSubjectId">
    /// For an app, the member who granted it: the default audience, and whose owner or administrator
    /// role allows naming an audience. Null for the operator.
    /// </param>
    Task<ExternalNotificationOutcome<ExternalNotificationSendResult>> SendAsync(
        ExternalNotificationSender sender,
        TenantContext tenant,
        ExternalNotificationRequest request,
        Guid? grantingSubjectId,
        CancellationToken ct);

    /// <summary>
    /// Archives the notices of <paramref name="key"/> for every member that has one, as
    /// <see cref="NotificationArchiveReason.Withdrawn"/>. The key stays used: sending it again sends
    /// nothing.
    /// </summary>
    Task<ExternalNotificationOutcome<ExternalNotificationWithdrawResult>> WithdrawAsync(
        ExternalNotificationSender sender,
        TenantContext tenant,
        string key,
        CancellationToken ct);
}

public sealed class ExternalNotificationService(
    IServiceScopeFactory scopes,
    IExternalNotificationLedger ledger,
    ITenantMembershipRepository memberships,
    TimeProvider clock,
    ILogger<ExternalNotificationService> logger) : IExternalNotificationService
{
    internal const string AuditEndpoint = "service:external-notifications";

    public const string Icon = "message-square";

    public async Task<ExternalNotificationOutcome<ExternalNotificationSendResult>> SendAsync(
        ExternalNotificationSender sender,
        TenantContext tenant,
        ExternalNotificationRequest request,
        Guid? grantingSubjectId,
        CancellationToken ct)
    {
        if (!tenant.IsActive)
            return Refused<ExternalNotificationSendResult>(ExternalNotificationRefusal.TenantInactive);

        var now = clock.GetUtcNow().UtcDateTime;
        if (ExternalNotificationRules.Validate(request, sender.LinkDomains, now, out var expiresAt) is { } invalid)
            return Refused<ExternalNotificationSendResult>(invalid);

        var people = await memberships.ListLivePeopleAsync(tenant.TenantId, now, ct);
        var audience = ResolveAudience(sender, request.Audience, grantingSubjectId, people);
        if (audience.Refusal is { } refusal)
            return Refused<ExternalNotificationSendResult>(refusal);

        var claim = await ledger.ClaimAsync(
            sender, tenant.TenantId, request.Key, audience.Recipients, sender.DailyLimit, now, ct);
        if (claim.RetryAfter is { } retryAfter)
        {
            return Refused<ExternalNotificationSendResult>(new ExternalNotificationRefusal(
                StatusCodes.Status429TooManyRequests,
                "rate_limited",
                $"An app may send {sender.DailyLimit} new notices to a site in a day.",
                retryAfter));
        }

        await using var scope = TenantJobScope.Open(scopes, tenant, AuditEndpoint);
        var notifications = scope.ServiceProvider.GetRequiredService<IInAppNotificationService>();
        var stored = scope.ServiceProvider.GetRequiredService<IInAppNotificationRepository>();

        var results = new List<ExternalNotificationRecipient>();
        foreach (var recipient in audience.Recipients)
        {
            var entry = claim.Entries.First(e => e.SubjectId == recipient);
            results.Add(entry.IsNew
                ? await CreateAsync(notifications, sender, tenant, request, expiresAt, entry, ct)
                : await ExistingAsync(stored, entry, ct));
        }

        return ExternalNotificationOutcome<ExternalNotificationSendResult>.Done(
            new ExternalNotificationSendResult(results.Count, results));
    }

    public async Task<ExternalNotificationOutcome<ExternalNotificationWithdrawResult>> WithdrawAsync(
        ExternalNotificationSender sender,
        TenantContext tenant,
        string key,
        CancellationToken ct)
    {
        if (!ExternalNotificationRules.IsValidKey(key))
        {
            return Refused<ExternalNotificationWithdrawResult>(ExternalNotificationRefusal.BadRequest(
                "invalid_key",
                $"The key must be 1 to {ExternalNotificationRules.MaxKeyLength} letters, digits, '.', '_', ':' or '-'."));
        }

        var entries = await ledger.ListAsync(sender, tenant.TenantId, key, ct);
        if (entries.Count == 0)
        {
            return Refused<ExternalNotificationWithdrawResult>(new ExternalNotificationRefusal(
                StatusCodes.Status404NotFound, "unknown_key", "This sender sent no notice with this key to this site."));
        }

        await using var scope = TenantJobScope.Open(scopes, tenant, AuditEndpoint);
        var notifications = scope.ServiceProvider.GetRequiredService<IInAppNotificationService>();

        var now = clock.GetUtcNow().UtcDateTime;
        var withdrawn = 0;
        foreach (var entry in entries.Where(e => e.WithdrawnAt is null))
        {
            if (entry.NotificationId is { } notificationId)
            {
                await notifications.ArchiveNotificationAsync(
                    notificationId, NotificationArchiveReason.Withdrawn, entry.SubjectId.ToString(), ct);
            }

            if (await ledger.MarkWithdrawnAsync(entry.Id, now, ct))
                withdrawn++;
        }

        logger.LogInformation(
            "{SenderKind} {SenderId} withdrew notice {Key} from {Count} members of tenant {TenantId}",
            sender.KindName, sender.Id, key, withdrawn, tenant.TenantId);

        return ExternalNotificationOutcome<ExternalNotificationWithdrawResult>.Done(
            new ExternalNotificationWithdrawResult(withdrawn));
    }

    private static (IReadOnlyList<Guid> Recipients, ExternalNotificationRefusal? Refusal) ResolveAudience(
        ExternalNotificationSender sender,
        ExternalNotificationAudience? audience,
        Guid? grantingSubjectId,
        IReadOnlyList<TenantMemberRecord> people)
    {
        if (sender.Kind == ExternalSenderKind.Operator && audience is null)
        {
            return ([], ExternalNotificationRefusal.BadRequest(
                "audience_required", "Name the audience: owners or members."));
        }

        if (sender.Kind == ExternalSenderKind.App)
        {
            var granter = people.FirstOrDefault(p => p.SubjectId == grantingSubjectId);
            if (audience is null)
                return (granter is null ? [] : [granter.SubjectId], null);

            if (granter is null || !granter.Roles.Any(r => r.Slug is RoleSeeds.Owner or RoleSeeds.Admin))
            {
                return ([], new ExternalNotificationRefusal(
                    StatusCodes.Status403Forbidden,
                    "audience_not_allowed",
                    "Only an app that an owner or an administrator connected may send to the owners or the members."));
            }
        }

        var recipients = audience == ExternalNotificationAudience.Owners
            ? people.Where(p => p.Roles.Any(r => r.Slug == RoleSeeds.Owner))
            : people;
        return (recipients.Select(p => p.SubjectId).Distinct().ToList(), null);
    }

    private async Task<ExternalNotificationRecipient> CreateAsync(
        IInAppNotificationService notifications,
        ExternalNotificationSender sender,
        TenantContext tenant,
        ExternalNotificationRequest request,
        DateTime expiresAt,
        ExternalNotificationEntry entry,
        CancellationToken ct)
    {
        var userId = entry.SubjectId.ToString();
        InAppNotificationDto created;
        try
        {
            created = await notifications.CreateNotificationAsync(
                userId: userId,
                type: sender.NotificationType,
                title: request.Title,
                category: NotificationCategory.Informational,
                urgency: NotificationUrgency.Info,
                icon: Icon,
                source: sender.NotificationSource,
                subtitle: request.Body,
                sourceId: request.Key,
                actions: Actions(request.Link),
                resolutionConditions: new ResolutionConditions { ExpiresAt = expiresAt },
                metadata: Metadata(sender, request.Link),
                cancellationToken: ct);
        }
        catch
        {
            await ledger.ReleaseAsync(entry.Id, CancellationToken.None);
            throw;
        }

        if (!await ledger.RecordNotificationAsync(entry.Id, created.Id, CancellationToken.None))
        {
            await notifications.ArchiveNotificationAsync(
                created.Id, NotificationArchiveReason.Withdrawn, userId, CancellationToken.None);
        }

        logger.LogInformation(
            "{SenderKind} {SenderId} sent notice {Key} to a member of tenant {TenantId}",
            sender.KindName, sender.Id, request.Key, tenant.TenantId);

        return new ExternalNotificationRecipient(entry.SubjectId, ExternalNotificationDelivery.Created, created.Id);
    }

    /// <summary>
    /// A row an earlier send claimed. Its notification is reported while it exists, archived or not;
    /// a claim still being created by a concurrent send has no notification yet and is reported as created.
    /// </summary>
    private static async Task<ExternalNotificationRecipient> ExistingAsync(
        IInAppNotificationRepository stored, ExternalNotificationEntry entry, CancellationToken ct)
    {
        if (entry.NotificationId is not { } notificationId)
            return new ExternalNotificationRecipient(entry.SubjectId, ExternalNotificationDelivery.Created, null);

        return await stored.GetByIdAsync(notificationId, ct) is null
            ? new ExternalNotificationRecipient(entry.SubjectId, ExternalNotificationDelivery.AlreadySent, null)
            : new ExternalNotificationRecipient(entry.SubjectId, ExternalNotificationDelivery.Created, notificationId);
    }

    private static List<NotificationActionDto> Actions(ExternalNotificationLink? link)
    {
        var actions = new List<NotificationActionDto>();
        if (link is not null)
            actions.Add(new NotificationActionDto { ActionId = "navigate", Label = link.Label, Variant = "default" });
        actions.Add(new NotificationActionDto { ActionId = "dismiss", Label = "dismiss", Variant = "outline" });
        return actions;
    }

    private static Dictionary<string, object> Metadata(ExternalNotificationSender sender, ExternalNotificationLink? link)
    {
        var metadata = new Dictionary<string, object>
        {
            [ExternalNotificationSender.SenderKindKey] = sender.KindName,
            [ExternalNotificationSender.SenderNameKey] = sender.Name,
        };
        if (link is not null)
        {
            metadata[ExternalNotificationSender.LinkUrlKey] = link.Url;
            metadata[ExternalNotificationSender.LinkLabelKey] = link.Label;
        }

        return metadata;
    }

    private static ExternalNotificationOutcome<T> Refused<T>(ExternalNotificationRefusal refusal) where T : class =>
        ExternalNotificationOutcome<T>.Refused(refusal);
}
