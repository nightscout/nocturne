using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <inheritdoc cref="ISubjectDeletion"/>
/// <remarks>
/// The delete runs on one unit (<see cref="SubjectRepository.DeleteAsync(Guid, bool, Func{Guid, CancellationToken, Task}, CancellationToken)"/>),
/// which stages each tenant's stop (<see cref="MadeByStop"/>) once nothing refuses it, so the stop
/// commits with the delete or not at all, and no reader sees what it stopped while the subject still
/// exists. Sessions are evicted once the unit commits. The teams and sites where the subject is staff
/// are read before anything goes, since their rows go with the account, and their owners' waiting
/// notices are recounted once it is deleted. The notices the delete removed from other people are
/// broadcast as archived once it commits, each from a scope opened for its site, so an open app drops them.
/// </remarks>
public sealed class SubjectDeletion(
    INocturneDbContextFactory contexts,
    ISubjectRepository subjects,
    IAccountLinkRoute linkRoute,
    GrantRevocationService grantRevocation,
    IAuthAuditService audit,
    TimeProvider time,
    TeamStaffAnnouncer teamStaff,
    SiteNotices sites,
    ILogger<SubjectDeletion> logger) : ISubjectDeletion
{
    private const string AuditEndpoint = "service:subject-deletion";

    /// <inheritdoc/>
    public async Task<SubjectDeletionOutcome> DeleteAsync(Guid subjectId, bool evenLastOwner, CancellationToken ct)
    {
        var subject = await subjects.GetAsync(subjectId, ct);
        if (subject is null)
            return new SubjectDeletionOutcome(new SubjectChange.NotFound(), []);
        if (subject.IsSystemSubject)
        {
            logger.LogWarning("Attempted to delete system subject {SubjectId}", subjectId);
            return new SubjectDeletionOutcome(new SubjectChange.NotFound(), []);
        }

        var staffNotices = await teamStaff.OwedForDeletionAsync(subjectId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var stopped = new List<OAuthGrantRecord>();
        await using var unit = await DbUnitOfWork.OpenAsync(contexts, ct);
        var outcome = await unit.CommitAsync(
            token =>
            {
                stopped.Clear();
                return new SubjectRepository(unit, linkRoute, time).DeleteAsync(
                    subjectId,
                    evenLastOwner,
                    async (tenantId, inTenant) =>
                        stopped.AddRange(await MadeByStop.StopAsync(unit, grantRevocation, tenantId, subjectId, now, inTenant)),
                    token);
            },
            ct: ct);

        if (outcome.Change is SubjectChange.Changed changed)
        {
            grantRevocation.EvictSessions(stopped);
            logger.LogInformation("Deleted subject {SubjectId}", subjectId);
            await audit.LogAsync(AuthAuditEventType.SubjectDeleted, subjectId, success: true);
            await teamStaff.AnnounceAsync(staffNotices);
            await BroadcastRemovedAsync(changed.RemovedNotices);
        }

        return outcome;
    }

    private async Task BroadcastRemovedAsync(IReadOnlyList<RemovedNotice> removed)
    {
        foreach (var site in removed.GroupBy(n => n.TenantId))
        {
            await SiteNotices.BestEffortAsync(
                () => sites.RunAsync<ISignalRBroadcastService>(site.Key, AuditEndpoint, async broadcast =>
                {
                    foreach (var notice in site)
                        await broadcast.BroadcastNotificationArchivedAsync(notice.UserId, notice.Notice, NotificationArchiveReason.ConditionMet);
                }),
                ex => logger.LogWarning(ex, "The notices a deletion removed were not broadcast on site {TenantId}", site.Key));
        }
    }
}
