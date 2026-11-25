using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Whether each of a tenant's alert channels delivers at one instant (<see cref="AlertChannelState"/>).
/// Delivery, the rule responses, low coverage and the owner's notice all read it from here.
/// </summary>
/// <remarks>
/// A channel is judged by where its destination comes from (<see cref="ChannelDestinations.ModeOf"/>):
/// <list type="bullet">
///   <item>
///     A channel that goes to a member, an <c>in_app</c> channel or a direct message, delivers
///     while that member may receive (<see cref="InAppRecipients"/>): a live membership whose
///     roles or permissions grant any access. A member who may read nothing gets no glucose
///     values by either. An <c>in_app</c> channel names the member; a direct message's member is
///     the one its destination is linked to in this tenant. The bot hands a destination to the
///     platform as it is, so without this a removed member, or one whose access has ended, would
///     keep receiving the tenant's alerts in their own chat.
///   </item>
///   <item>A channel whose destination was typed in delivers until its stop (<see cref="AlertRuleChannelSnapshot.StopsAt"/>).</item>
///   <item>A device channel, and one an integration made, always delivers.</item>
/// </list>
/// Nothing is deleted when a channel does not deliver, so it delivers again with no edit once its
/// member may receive again. Removal deletes a member's chat links, so a member who joins again
/// must link their chat account again before a direct message reaches them.
/// </remarks>
public sealed class AlertChannelStates
{
    private readonly IReadOnlySet<Guid>? _receivers;
    private readonly IReadOnlyDictionary<(string Platform, string PlatformUserId), Guid> _links;
    private readonly DateTime _now;

    private AlertChannelStates(
        IReadOnlySet<Guid>? receivers,
        IReadOnlyDictionary<(string, string), Guid> links,
        DateTime now)
    {
        _receivers = receivers;
        _links = links;
        _now = now;
    }

    /// <summary>
    /// Reads what judging <paramref name="channels"/> of <paramref name="tenantId"/> at
    /// <paramref name="now"/> needs, and nothing a channel of another kind would.
    /// </summary>
    public static async Task<AlertChannelStates> LoadAsync(
        IEnumerable<AlertRuleChannelSnapshot> channels,
        Guid tenantId,
        DateTime now,
        ITenantMembershipRepository members,
        INocturneDbContextFactory contexts,
        CancellationToken ct)
    {
        var modes = channels.Select(c => ChannelDestinations.ModeOf(c.ChannelType)).ToHashSet();
        var linked = modes.Contains(ChannelDestinationMode.LinkedIdentity);
        if (!linked && !modes.Contains(ChannelDestinationMode.Member))
            return new AlertChannelStates(new HashSet<Guid>(), new Dictionary<(string, string), Guid>(), now);

        var receivers = (await members.ListLivePeopleAsync(tenantId, now, ct))
            .Where(InAppRecipients.MayReceive)
            .Select(m => m.SubjectId)
            .ToHashSet();

        var links = new Dictionary<(string, string), Guid>();
        if (linked)
        {
            await using var db = await contexts.CreatePlatformAsync(ct);
            foreach (var link in await ChatIdentityDirectoryService.ReachableLinksOfTenant(db, tenantId)
                         .AsNoTracking()
                         .Select(d => new { d.Platform, d.PlatformUserId, d.NocturneUserId })
                         .ToListAsync(ct))
                links.TryAdd((link.Platform, link.PlatformUserId), link.NocturneUserId);
        }

        return new AlertChannelStates(receivers, links, now);
    }

    /// <summary>
    /// The states when the members and chat links could not be read. A channel judged from its
    /// own row is judged as ever; a channel that goes to a member is not confirmed, so it is not
    /// sent. Only delivery reads this, so that a failed read does not hold back the rest of a fire.
    /// </summary>
    public static AlertChannelStates Unread(DateTime now) =>
        new(null, new Dictionary<(string, string), Guid>(), now);

    /// <summary>
    /// The channel's state, the member it goes to when it names one, and, when it does not deliver,
    /// why, worded for a delivery's error.
    /// </summary>
    public AlertChannelVerdict Of(AlertRuleChannelSnapshot channel)
    {
        switch (ChannelDestinations.ModeOf(channel.ChannelType))
        {
            case ChannelDestinationMode.Member or ChannelDestinationMode.LinkedIdentity when _receivers is null:
                return new(AlertChannelState.NoRecipient, null,
                    "Not sent: the members of this site could not be read");

            case ChannelDestinationMode.Member:
                if (!Guid.TryParse(channel.Destination, out var named))
                    return new(AlertChannelState.NoRecipient, null, "In-app channel names no recipient");
                return _receivers!.Contains(named)
                    ? new(AlertChannelState.Active, named, null)
                    : new(AlertChannelState.NoRecipient, named,
                        "In-app recipient is not a member of this site who holds any access");

            case ChannelDestinationMode.LinkedIdentity:
                var platform = ChannelDestinations.PlatformOf(channel.ChannelType)!;
                var account = AlertRuleChannelWriter.PlatformDisplayName(platform);
                if (!_links.TryGetValue((platform, channel.Destination), out var linked))
                    return new(AlertChannelState.NoRecipient, channel.RecipientSubjectId,
                        $"No recipient: no member of this site has linked this {account} account");
                return _receivers!.Contains(linked)
                    ? new(AlertChannelState.Active, linked, null)
                    : new(AlertChannelState.NoRecipient, linked,
                        $"No recipient: the member linked to this {account} account is not a member of this site who holds any access");

            case ChannelDestinationMode.UserSupplied when MembershipLiveness.HasEnded(channel.StopsAt, _now):
                return new(AlertChannelState.Stopped, null,
                    $"Stopped at {channel.StopsAt:O}: the access of the member who added this channel has ended");

            default:
                return new(AlertChannelState.Active, null, null);
        }
    }
}

/// <summary>
/// What <see cref="AlertChannelStates.Of"/> decides for one channel: its state; the member an
/// <c>in_app</c> channel names, or the member a direct message's account is linked to, or was when
/// the channel was saved, null for other channels and when unknown; and why the channel does not
/// deliver, null when it does.
/// </summary>
public sealed record AlertChannelVerdict(AlertChannelState State, Guid? MemberSubjectId, string? Reason)
{
    public bool IsActive => State == AlertChannelState.Active;
}
