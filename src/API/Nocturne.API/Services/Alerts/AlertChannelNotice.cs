using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Tells the tenant's owners, by an in-app notification per channel, that a channel of an enabled
/// rule does not send (<see cref="AlertChannelStates"/>), and archives the notification once the
/// channel sends again, is removed from its rule, or its rule is disabled or deleted.
/// </summary>
/// <remarks>
/// <para>
/// Without it, a family whose only low alert went to a member who has since left would see the
/// rule on and believe they are covered. Only <see cref="AlertSweepJob"/> calls it, beside
/// <see cref="IUnevaluableRuleNotice"/>, so it adds nothing to the scheduler and nothing to an
/// evaluation.
/// </para>
/// <para>
/// A notification is keyed by the channel's id, and creation is atomic across runs and replicas
/// (<see cref="IInAppNotificationService.CreateNotificationUnlessActiveAsync"/>).
/// <see cref="AlertChannelNoticeLog"/> keeps, per tenant, the channels last reconciled for up to
/// <see cref="AlertChannelNoticeLog.Freshness"/>, so a run that finds the same channels reads no
/// owner and no notification.
/// </para>
/// </remarks>
public interface IAlertChannelNotice
{
    /// <summary>Reconciles the owners' notifications with the channels of <paramref name="enabled"/>, every enabled rule of the tenant, at <paramref name="now"/>.</summary>
    Task CheckAsync(IReadOnlyList<AlertRuleSnapshot> enabled, DateTime now, CancellationToken ct);
}

/// <summary>Per tenant, the channels <see cref="AlertChannelNotice"/> last found not sending.</summary>
public sealed class AlertChannelNoticeLog(TimeProvider timeProvider)
{
    /// <summary>How long a reconcile is trusted, which bounds how late an owner's archived notification is raised again.</summary>
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, (IReadOnlySet<Guid> Channels, long At)> _reconciled = new();

    internal bool Unchanged(Guid tenantId, IReadOnlySet<Guid> channels) =>
        _reconciled.TryGetValue(tenantId, out var seen)
        && timeProvider.GetElapsedTime(seen.At) < Freshness
        && seen.Channels.SetEquals(channels);

    internal void Record(Guid tenantId, IReadOnlySet<Guid> channels) =>
        _reconciled[tenantId] = (channels, timeProvider.GetTimestamp());
}

internal sealed class AlertChannelNotice(
    IAlertRuleRepository rules,
    ITenantMembershipRepository members,
    INocturneDbContextFactory contexts,
    ITenantOwnerResolver ownerResolver,
    IInAppNotificationService notifications,
    ITenantAccessor tenantAccessor,
    AlertChannelNoticeLog log,
    ILogger<AlertChannelNotice> logger)
    : IAlertChannelNotice
{
    internal const string NotificationType = "alerts.channelNotSending";
    internal const string NotificationSource = "alert-channel";

    public async Task CheckAsync(IReadOnlyList<AlertRuleSnapshot> enabled, DateTime now, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty) return;

        var channels = enabled.Count == 0
            ? new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>>()
            : await rules.GetChannelsAsync(enabled.Select(r => r.Id).ToList(), ct);
        var states = await AlertChannelStates.LoadAsync(
            channels.Values.SelectMany(c => c), tenantId, now, members, contexts, ct);
        var names = enabled.ToDictionary(r => r.Id, r => r.Name);
        var notSending = channels.Values
            .SelectMany(c => c)
            .Select(c => (Channel: c, Verdict: states.Of(c)))
            .Where(c => !c.Verdict.IsActive)
            .ToDictionary(c => c.Channel.Id.ToString(), c => c);

        var keys = notSending.Values.Select(c => c.Channel.Id).ToHashSet();
        if (log.Unchanged(tenantId, keys))
            return;

        foreach (var owner in await ownerResolver.GetOwnerSubjectIdsAsync(tenantId, ct))
        {
            var standing = (await notifications.GetActiveNotificationsAsync(owner, ct))
                .Where(n => n.Type == NotificationType && n.SourceId is not null)
                .Select(n => n.SourceId!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (sourceId, (channel, verdict)) in notSending.Where(c => !standing.Contains(c.Key)))
            {
                if (await notifications.CreateNotificationUnlessActiveAsync(
                        owner, NotificationType, TitleKey(verdict.State), sourceId,
                        category: NotificationCategory.Alert,
                        source: NotificationSource,
                        subtitle: TitleKey(verdict.State) + "_subtitle",
                        metadata: Metadata(channel, verdict, names.GetValueOrDefault(channel.AlertRuleId)),
                        cancellationToken: ct))
                    logger.LogWarning(
                        "Channel {ChannelId} of alert rule {AlertRuleId} for tenant {TenantId} does not send: {Reason}",
                        channel.Id, channel.AlertRuleId, tenantId, verdict.Reason);
            }

            foreach (var sourceId in standing.Where(s => !notSending.ContainsKey(s)))
                await notifications.ArchiveBySourceAsync(
                    owner, NotificationType, sourceId, NotificationArchiveReason.ConditionMet, ct);
        }

        log.Record(tenantId, keys);
    }

    private static string TitleKey(AlertChannelState state) =>
        state == AlertChannelState.Stopped ? "alert_channel_stopped" : "alert_channel_no_recipient";

    private static Dictionary<string, object> Metadata(
        AlertRuleChannelSnapshot channel, AlertChannelVerdict verdict, string? ruleName)
    {
        var metadata = new Dictionary<string, object>
        {
            ["alertRuleId"] = channel.AlertRuleId.ToString(),
            ["channelId"] = channel.Id.ToString(),
            ["channelType"] = JsonSerializer.Serialize(channel.ChannelType).Trim('"'),
            ["state"] = JsonSerializer.Serialize(verdict.State).Trim('"'),
        };
        if (ruleName is not null)
            metadata["alertRuleName"] = ruleName;
        if (channel.DestinationLabel is not null)
            metadata["destinationLabel"] = channel.DestinationLabel;
        if (verdict.MemberSubjectId is { } member)
            metadata["memberSubjectId"] = member.ToString();
        if (channel.AddedBySubjectId is { } adder)
            metadata["addedBySubjectId"] = adder.ToString();
        if (channel.StopsAt is { } stopsAt)
            metadata["stopsAt"] = stopsAt.ToString("O", CultureInfo.InvariantCulture);
        return metadata;
    }
}
