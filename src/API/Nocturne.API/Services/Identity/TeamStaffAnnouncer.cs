using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Tells the owners of a site how many of a team's staff wait for their approval, and that a team's staff
/// newly reach the site.
/// </summary>
/// <remarks>
/// <para>
/// One quiet notification per owner in force at the site (<see cref="ITenantMembershipRepository.ListLivePeopleAsync"/>)
/// and team, so a large team fills no more of the app's limit on active notices of one source than a small
/// one. Its title is a key the web words, so it never reaches a device
/// (<see cref="Nocturne.Core.Models.ClientDevices.DeviceNotificationMirror"/>). The waiting and the joined notice
/// each have a source of their own, apart from <see cref="TeamRenameAnnouncer"/>'s, so no notice of one kind can
/// push out a notice of another. Best-effort, as that announcer: a site whose notices cannot be filed is logged
/// and skipped.
/// </para>
/// <para>
/// A site that trusts the whole team (<see cref="TeamStaffRule.TeamTrusted"/>) is told nothing of its staff: no
/// staff wait there, no joined notice is filed, and a recount takes back the joined notice with the waiting one.
/// </para>
/// <para>
/// The waiting notice states a count read after the write has committed, under a lock on the team at the site
/// (<see cref="ITeamRepository.WithStaffWaitingLockedAsync"/>) held until every owner's notice is filed, with the
/// site's owners read under it too. So the last recount to take it reads every earlier commit and files the
/// count they left. A staff member who owns the site is not counted: the team gives them nothing, and an owner
/// who steps down is counted by <see cref="RecountOwnerChangeAsync"/>.
/// </para>
/// </remarks>
public sealed partial class TeamStaffAnnouncer(
    ITeamRepository teams,
    ITenantMembershipRepository members,
    SiteNotices sites,
    TimeProvider time,
    ILogger<TeamStaffAnnouncer> logger)
{
    /// <summary>The source of <see cref="InAppNotificationType.TeamStaffWaiting"/>.</summary>
    public const string WaitingSource = "team-staff";

    /// <summary>The source of <see cref="InAppNotificationType.TeamStaffJoined"/>.</summary>
    public const string JoinedSource = "team-staff-joined";

    /// <summary>The team's current name.</summary>
    public const string TeamKey = "team";

    /// <summary>The team's id, which the web links to.</summary>
    public const string TeamIdKey = "teamId";

    /// <summary>How many of the team's staff wait at the site.</summary>
    public const string CountKey = "count";

    private const string AuditEndpoint = "service:team-staff-announcer";

    /// <summary>The source id of the notices about a team at a site, whose notices are one per owner.</summary>
    public static string SourceOf(Guid teamId) => teamId.ToString();

    /// <summary>Recounts and files the notices a committed write owes, a site at a time. Never throws for a store failure or a cancellation.</summary>
    public async Task AnnounceAsync(StaffNotices notices)
    {
        foreach (var site in notices.Sites)
        {
            await SiteNotices.BestEffortAsync(
                () => AnnounceAtAsync(
                    site,
                    notices.Recount.Where(k => k.TenantId == site).Select(k => k.TeamId).ToHashSet(),
                    notices.Joined.Where(k => k.TenantId == site).Select(k => k.TeamId).ToHashSet(),
                    null),
                ex => LogNotAnnounced(ex, site));
        }
    }

    /// <summary>
    /// Recounts the waiting staff of every team with access to <paramref name="tenantId"/>, after
    /// <paramref name="subjectId"/> may have become or stopped being an owner there: a new owner is told of
    /// staff who already wait, and a former owner's waiting notices are taken back.
    /// </summary>
    public Task RecountOwnerChangeAsync(Guid tenantId, Guid subjectId) =>
        SiteNotices.BestEffortAsync(
            async () =>
            {
                var accesses = await teams.ListAccessOfTenantAsync(tenantId, time.GetUtcNow().UtcDateTime, CancellationToken.None);
                await AnnounceAtAsync(tenantId, accesses.Select(a => a.TeamId).ToHashSet(), new HashSet<Guid>(), subjectId);
            },
            ex => LogNotAnnounced(ex, tenantId));

    /// <summary>The recounts a deletion of <paramref name="subjectId"/>'s account owes: each team and site where they are staff, read before it.</summary>
    public async Task<StaffNotices> OwedForDeletionAsync(Guid subjectId, CancellationToken ct) =>
        new(await teams.ListStaffSitesOfSubjectAsync(subjectId, ct), []);

    private async Task AnnounceAtAsync(Guid site, HashSet<Guid> recount, HashSet<Guid> joined, Guid? ownerChanged)
    {
        foreach (var team in recount.Union(joined).Order())
        {
            await teams.WithStaffWaitingLockedAsync(
                site,
                team,
                time.GetUtcNow().UtcDateTime,
                waiting => FileAsync(site, team, waiting, recount.Contains(team), joined.Contains(team), ownerChanged),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// When <paramref name="recount"/>, brings each owner's waiting notice for <paramref name="team"/> into line
    /// with the staff who wait now: taken back at none, filed with the count when there is none, and replaced when
    /// the count differs, so an owner who read the old count is told of the new one. Takes back the waiting notice
    /// of <paramref name="ownerChanged"/> when they no longer own the site, and files the joined notice when
    /// <paramref name="joined"/>. While the site trusts the team, a recount takes back each owner's joined notice
    /// and none is filed.
    /// </summary>
    private async Task FileAsync(Guid site, Guid team, TeamStaffWaiting? waiting, bool recount, bool joined, Guid? ownerChanged)
    {
        var owners = (await members.ListLivePeopleAsync(site, time.GetUtcNow().UtcDateTime, CancellationToken.None))
            .Where(m => m.IsOwner)
            .Select(m => m.SubjectId)
            .ToHashSet();
        var count = waiting?.WaitingSubjectIds.Count(subject => !owners.Contains(subject)) ?? 0;

        await sites.RunAsync<IInAppNotificationService>(site, AuditEndpoint, async notifications =>
        {
            foreach (var owner in owners.Order())
            {
                if (recount)
                    await FileWaitingAsync(notifications, owner.ToString(), team, waiting?.TeamName, count);
                if (recount && waiting is { Trusted: true })
                    await notifications.ArchiveBySourceAsync(
                        owner.ToString(), InAppNotificationType.TeamStaffJoined.ToWireString(), SourceOf(team), NotificationArchiveReason.ConditionMet, CancellationToken.None);
                if (joined && waiting is { Trusted: false })
                    await notifications.CreateNotificationUnlessActiveAsync(
                        owner.ToString(), InAppNotificationType.TeamStaffJoined.ToWireString(), NotificationLabel.TeamStaffJoined.ToWireString(), SourceOf(team),
                        metadata: new Dictionary<string, object> { [TeamKey] = waiting.TeamName, [TeamIdKey] = team },
                        cancellationToken: CancellationToken.None);
            }

            if (ownerChanged is { } subject && !owners.Contains(subject))
                await FileWaitingAsync(notifications, subject.ToString(), team, waiting?.TeamName, 0);
        });
    }

    private static async Task FileWaitingAsync(IInAppNotificationService notifications, string user, Guid team, string? teamName, int count)
    {
        var source = SourceOf(team);
        var live = (await notifications.GetActiveNotificationsAsync(user, CancellationToken.None))
            .FirstOrDefault(n => n.Type == InAppNotificationType.TeamStaffWaiting.ToWireString() && n.SourceId == source);
        if (count == 0)
        {
            if (live is not null)
                await notifications.ArchiveBySourceAsync(user, InAppNotificationType.TeamStaffWaiting.ToWireString(), source, NotificationArchiveReason.ConditionMet, CancellationToken.None);
            return;
        }

        if (live is not null)
        {
            if (int.TryParse(live.Metadata?.GetValueOrDefault(CountKey)?.ToString(), out var standing) && standing == count)
                return;

            await notifications.ArchiveBySourceAsync(user, InAppNotificationType.TeamStaffWaiting.ToWireString(), source, NotificationArchiveReason.Superseded, CancellationToken.None);
        }

        await notifications.CreateNotificationUnlessActiveAsync(
            user, InAppNotificationType.TeamStaffWaiting.ToWireString(), NotificationLabel.TeamStaffWaiting.ToWireString(), source,
            metadata: new Dictionary<string, object>
            {
                [TeamKey] = teamName ?? string.Empty,
                [TeamIdKey] = team,
                [CountKey] = count,
            },
            cancellationToken: CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The notices about a team's staff were not filed on site {TenantId}.")]
    private partial void LogNotAnnounced(Exception ex, Guid tenantId);
}
