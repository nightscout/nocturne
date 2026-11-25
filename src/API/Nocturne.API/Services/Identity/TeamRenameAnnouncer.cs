using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Tells each person who gave a team access to a site that the team has a new name, so a site's
/// owner is not left wondering who "the new name" is.
/// </summary>
/// <remarks>
/// One quiet notification per team and site, to the approver while they are still a member of that
/// site in force, filed in a scope opened for the site. It names the team by the name the approver
/// knew, the one they approved or last saw in a notice they dismissed, and by its current name. A
/// later rename while the notice is live replaces it with a new notice, announced as any new one
/// is, that keeps the name they knew; a rename back to that name removes it. Best-effort: the rename
/// has committed, and a site whose notification cannot be filed is logged and skipped.
/// </remarks>
public sealed partial class TeamRenameAnnouncer(
    ITeamRepository teams,
    ITenantMembershipRepository members,
    SiteNotices sites,
    TimeProvider time,
    ILogger<TeamRenameAnnouncer> logger)
{
    public const string NotificationType = "team.renamed";

    public const string Source = "teams";

    /// <summary>The title, a key the web words with <see cref="OldNameKey"/> and <see cref="NewNameKey"/>.</summary>
    public const string TitleKey = "team_renamed";

    /// <summary>The name the approver knew.</summary>
    public const string OldNameKey = "oldName";

    /// <summary>The team's current name.</summary>
    public const string NewNameKey = "newName";

    private const string AuditEndpoint = "service:team-rename-announcer";

    /// <summary>
    /// A concurrent rename can file its notice between this one's read and its create; the create
    /// then reads again, a bounded number of times.
    /// </summary>
    private const int Attempts = 3;

    /// <summary>
    /// Files the notifications for a rename from <paramref name="oldName"/>, naming the team by its
    /// current name as the approvers are read. Never throws for a store failure or a cancellation.
    /// </summary>
    public Task AnnounceAsync(Guid teamId, string oldName) =>
        SiteNotices.BestEffortAsync(async () =>
        {
            foreach (var approver in await teams.ListAccessApproversAsync(teamId, CancellationToken.None))
            {
                await SiteNotices.BestEffortAsync(
                    () => AnnounceToAsync(teamId, approver, oldName), ex => LogNotAnnounced(ex, teamId, approver.TenantId));
            }
        }, ex => LogNotAnnounced(ex, teamId, null));

    private async Task AnnounceToAsync(Guid teamId, TeamAccessApprover approver, string oldName)
    {
        if (!await members.IsMemberAsync(approver.TenantId, approver.SubjectId, time.GetUtcNow().UtcDateTime, CancellationToken.None))
            return;
        await sites.RunAsync<IInAppNotificationService>(approver.TenantId, AuditEndpoint, async notifications =>
        {
            var user = approver.SubjectId.ToString();
            var source = teamId.ToString();

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var live = (await notifications.GetActiveNotificationsAsync(user, CancellationToken.None))
                    .FirstOrDefault(n => n.Type == NotificationType && n.SourceId == source);
                var known = live?.Metadata?.GetValueOrDefault(OldNameKey)?.ToString() ?? oldName;
                if (live is not null)
                {
                    await notifications.ArchiveBySourceAsync(
                        user, NotificationType, source,
                        known == approver.TeamName ? NotificationArchiveReason.ConditionMet : NotificationArchiveReason.Superseded,
                        CancellationToken.None);
                }

                if (known == approver.TeamName)
                    return;

                if (await notifications.CreateNotificationUnlessActiveAsync(
                        user, NotificationType, TitleKey, source,
                        metadata: new Dictionary<string, object> { [OldNameKey] = known, [NewNameKey] = approver.TeamName },
                        cancellationToken: CancellationToken.None))
                    return;
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The rename of team {TeamId} was not announced on site {TenantId}.")]
    private partial void LogNotAnnounced(Exception ex, Guid teamId, Guid? tenantId);
}
