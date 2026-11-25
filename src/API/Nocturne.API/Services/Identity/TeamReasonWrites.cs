using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// The team writes that change what a team gives at its sites, each in one commit with the team reasons
/// it adds, sets and removes (<see cref="TeamReasonChanges"/>). A site stopping the team's access, the
/// team being deleted, and a staff member being removed or leaving remove the reasons that stand on what
/// goes. A staff member joining or being edited, a site approving the team's access, and a role a team's
/// access gives being edited, and an owner of a site changing what the team's access gives, approving or
/// declining a staff member or turning "the team manages its own staff" on or off, bring the reasons of the
/// staff they reach into line with the team's rows.
/// A member whose last reason goes loses the membership with what a direct removal takes
/// (<see cref="MembershipRemoval.StageLossAsync"/>), what they made stopped at once; a member whose end
/// moves earlier has what they made capped at it.
/// </summary>
/// <remarks>
/// Each site's rows are held to that site by row level security, so each site's part runs with the
/// unit pinned to it (<see cref="RlsPinningExtensions.InTenantForTransactionAsync{T}"/>) and is saved
/// before the next. What the member made is staged before their membership's own rows, as
/// <see cref="MembershipRemoval"/> orders them. Sessions of the grants revoked or capped are evicted
/// once the unit commits. A write that finds, under its locks, a staff member whose row it did not lock
/// first writes nothing, and runs again (<see cref="MembershipLock"/>).
/// </remarks>
internal static class TeamReasonWrites
{
    /// <summary>
    /// The outcome of a write that has committed, and <paramref name="Notices"/>, what it owes the owners of
    /// the sites once it has (<see cref="TeamStaffAnnouncer"/>).
    /// </summary>
    internal sealed record Written<T>(T Outcome, StaffNotices Notices);

    /// <summary>How many times <see cref="RunAsync{T}"/> runs a write that wrote nothing before it gives up.</summary>
    internal const int Attempts = 5;

    /// <summary>Stops the team's access to <paramref name="tenantId"/>. False when it has none.</summary>
    public static async Task<Written<bool>> StopAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid tenantId, Guid teamId, DateTime now,
        CancellationToken ct) =>
        await RunWithNoticesAsync<bool>(
            "stop",
            token => DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, token),
            async (unit, token) => await TeamRepository.LockAccessStopAsync(unit, tenantId, teamId, token) is { } changes
                ? (true, changes)
                : (false, null),
            grantRevocation, now, ct);

    /// <summary>
    /// Deletes the team with its members, invites and access to sites, recording each site's access as
    /// stopped. <see cref="TeamDeletion.NotFound"/> when the caller is not a member,
    /// <see cref="TeamDeletion.Refused"/> when the team may not be deleted now.
    /// </summary>
    public static Task<Written<TeamDeletion>> DeleteTeamAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid teamId, Guid callerSubjectId,
        DateTime now, CancellationToken ct) =>
        RunWithNoticesAsync<TeamDeletion>(
            "team deletion",
            token => DbUnitOfWork.OpenPlatformAsync(contexts, token),
            async (unit, token) => await TeamRepository.LockTeamDeletionAsync(unit, teamId, callerSubjectId, token),
            grantRevocation, now, ct);

    /// <summary>
    /// Removes the staff member from the team and every site's record of them; the caller's own id means
    /// leave. False when the subject is not staff of the team, or the caller is neither a manager of it
    /// nor that staff member. Never refused for the last-manager rule.
    /// </summary>
    public static async Task<Written<bool>> RemoveStaffAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid teamId, Guid callerSubjectId,
        Guid staffSubjectId, DateTime now, CancellationToken ct) =>
        await RunWithNoticesAsync<bool>(
            "staff removal",
            token => DbUnitOfWork.OpenPlatformAsync(contexts, token),
            async (unit, token) =>
                await TeamRepository.LockStaffRemovalAsync(unit, teamId, callerSubjectId, staffSubjectId, token) is { } changes
                    ? (true, changes)
                    : (false, null),
            grantRevocation, now, ct);

    /// <inheritdoc cref="TeamRepository.LockStaffJoinAsync"/>
    public static Task<Written<TeamInviteAcceptance>> JoinStaffAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, string tokenHash, Guid subjectId,
        DateTime now, CancellationToken ct) =>
        RunWithNoticesAsync<TeamInviteAcceptance>(
            "staff join",
            token => DbUnitOfWork.OpenPlatformAsync(contexts, token),
            async (unit, token) => await TeamRepository.LockStaffJoinAsync(unit, tokenHash, subjectId, now, token),
            grantRevocation, now, ct);

    /// <inheritdoc cref="TeamRepository.LockStaffUpdateAsync"/>
    public static Task<Written<TeamStaffChange>> UpdateStaffAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid teamId, Guid callerSubjectId,
        Guid staffSubjectId, TeamStaffNarrowing narrowing, DateTime now, CancellationToken ct) =>
        RunWithNoticesAsync<TeamStaffChange>(
            "staff edit",
            token => DbUnitOfWork.OpenPlatformAsync(contexts, token),
            async (unit, token) =>
                await TeamRepository.LockStaffUpdateAsync(unit, teamId, callerSubjectId, staffSubjectId, narrowing, now, token),
            grantRevocation, now, ct);

    /// <inheritdoc cref="TeamRepository.LockAccessApprovalAsync"/>
    public static Task<Written<TeamAccessApproval>> ApproveAccessAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, string tokenHash,
        TeamAccessApproverCredential approver, TeamAccessGrant grant, DateTime now, CancellationToken ct) =>
        RunWithNoticesAsync(
            "access approval",
            token => DbUnitOfWork.OpenPlatformAsync(contexts, token),
            (unit, token) => TeamRepository.LockAccessApprovalAsync(unit, tokenHash, approver, grant, now, token),
            grantRevocation, now, ct);

    /// <inheritdoc cref="TeamRepository.LockAccessEditAsync"/>
    public static Task<Written<TeamAccessEdit>> EditAccessAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid tenantId, Guid teamId,
        TeamAccessApproverCredential editor, TeamAccessTerms terms, DateTime now, CancellationToken ct) =>
        RunWithNoticesAsync(
            "access edit",
            token => DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, token),
            (unit, token) => TeamRepository.LockAccessEditAsync(unit, tenantId, teamId, editor, terms, now, token),
            grantRevocation, now, ct);

    /// <inheritdoc cref="TeamRepository.LockStaffDecisionAsync"/>
    public static Task<Written<OwnerDecision>> DecideStaffAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid tenantId, Guid teamId,
        Guid callerSubjectId, Guid staffSubjectId, string status, DateTime now, CancellationToken ct) =>
        RunWithNoticesAsync(
            "staff decision",
            token => DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, token),
            (unit, token) => TeamRepository.LockStaffDecisionAsync(unit, tenantId, teamId, callerSubjectId, staffSubjectId, status, now, token),
            grantRevocation, now, ct);

    /// <inheritdoc cref="TeamRepository.LockStaffRuleAsync"/>
    public static Task<Written<OwnerDecision>> SetStaffRuleAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid tenantId, Guid teamId,
        Guid callerSubjectId, bool teamManagesStaff, DateTime now, CancellationToken ct) =>
        RunWithNoticesAsync(
            "staff rule change",
            token => DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, token),
            (unit, token) => TeamRepository.LockStaffRuleAsync(unit, tenantId, teamId, callerSubjectId, teamManagesStaff, now, token),
            grantRevocation, now, ct);

    /// <inheritdoc cref="TenantMembershipRepository.LockRoleEditAsync"/>
    public static Task<TenantRoleUpdate> EditRoleAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid tenantId, Guid roleId,
        TenantRoleEdit edit, Guid? editorSubjectId, DateTime now, CancellationToken ct) =>
        RunAsync(
            "role edit",
            token => DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, token),
            (unit, token) => TenantMembershipRepository.LockRoleEditAsync(unit, tenantId, roleId, edit, editorSubjectId, now, token),
            grantRevocation, now, ct);

    /// <summary>
    /// Brings <paramref name="subjectId"/>'s team reasons on <paramref name="tenantId"/> into line with the
    /// team's rows (<see cref="TeamReasonChanges.SyncAsync"/>) and applies them (<see cref="ApplyAsync"/>),
    /// after a write on <paramref name="unit"/> that can change whether the subject owns the site, since an
    /// owner holds none. The caller has saved its write, holds what
    /// <see cref="TenantMembershipRepository.LockOwnerChangeAsync"/> takes, and evicts the sessions of the
    /// grants returned once the unit commits.
    /// </summary>
    public static async Task<IReadOnlyList<OAuthGrantRecord>> SyncOwnerChangeAsync(
        DbUnitOfWork unit, GrantRevocationService grantRevocation, Guid tenantId, Guid subjectId, DateTime now,
        CancellationToken ct) =>
        await ApplyAsync(unit, await TeamReasonChanges.SyncAsync(unit, [(tenantId, subjectId)], now, ct), grantRevocation, now, ct);

    /// <summary>
    /// Stages and saves <paramref name="changes"/> on <paramref name="unit"/>, inside its commit: for each
    /// site, pinned to it, the stop of what each member whose membership ends made there
    /// (<see cref="MadeByStop"/>), the reasons, and the rest of what those members lose; then the write the
    /// reasons stood on.
    /// </summary>
    /// <returns>The grants revoked or capped, for <see cref="GrantRevocationService.EvictSessions"/> once the unit commits.</returns>
    public static async Task<IReadOnlyList<OAuthGrantRecord>> ApplyAsync(
        DbUnitOfWork unit, TeamReasonChanges changes, GrantRevocationService grantRevocation, DateTime now, CancellationToken ct)
    {
        var lost = new List<OAuthGrantRecord>();
        foreach (var site in changes.Sites)
        {
            lost.AddRange(await unit.Context.InTenantForTransactionAsync(site, async token =>
            {
                var stops = new List<(Guid SubjectId, MadeByStop Made)>();
                foreach (var subjectId in changes.EndingAt(site))
                    stops.Add((subjectId, await MadeByStop.StageAsync(unit, site, subjectId, now, token)));

                List<OAuthGrantRecord> lostHere = [.. await changes.StageReasonsAtAsync(site, now, token)];
                foreach (var (subjectId, made) in stops)
                    lostHere.AddRange(await MembershipRemoval.StageLossAsync(unit, made, grantRevocation, site, subjectId, capAt: now, token));

                await unit.Context.SaveChangesAsync(token);
                return lostHere;
            }, ct));
        }

        await changes.StageAsync(ct);
        await unit.Context.SaveChangesAsync(ct);
        return lost;
    }

    /// <summary>
    /// Runs <paramref name="write"/> on a unit <paramref name="open"/> opens and applies the changes it
    /// returns (<see cref="ApplyAsync"/>) in the same commit, then evicts the sessions of what they revoked
    /// or capped. A write that returns null wrote nothing and runs again on a new unit, up to
    /// <see cref="Attempts"/> times. Returns with the outcome the notices the committed write owes the owners
    /// of the sites (<see cref="TeamReasonChanges.WithStaffNotices"/>), which the caller files.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="write"/> wrote nothing on every attempt: a staff member joined between each read and
    /// its locks, which only a flood of joins sustains.
    /// </exception>
    internal static async Task<Written<T>> RunWithNoticesAsync<T>(
        string name,
        Func<CancellationToken, ValueTask<DbUnitOfWork>> open,
        Func<DbUnitOfWork, CancellationToken, Task<(T Outcome, TeamReasonChanges? Changes)?>> write,
        GrantRevocationService grantRevocation, DateTime now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            await using var unit = await open(ct);
            var done = await unit.CommitAsync<(T Outcome, IReadOnlyList<OAuthGrantRecord> Lost, StaffNotices Notices)?>(
                async token => await write(unit, token) is { } written
                    ? (
                        written.Outcome,
                        written.Changes is { } changes ? await ApplyAsync(unit, changes, grantRevocation, now, token) : [],
                        written.Changes?.Notices ?? StaffNotices.None)
                    : null,
                ct: ct);
            if (done is { } result)
            {
                grantRevocation.EvictSessions(result.Lost);
                return new Written<T>(result.Outcome, result.Notices);
            }
        }

        throw new InvalidOperationException($"The {name} found a new staff member under its locks on each of {Attempts} attempts.");
    }

    /// <summary><see cref="RunWithNoticesAsync{T}"/> for a write that owes no notice.</summary>
    internal static async Task<T> RunAsync<T>(
        string name,
        Func<CancellationToken, ValueTask<DbUnitOfWork>> open,
        Func<DbUnitOfWork, CancellationToken, Task<(T Outcome, TeamReasonChanges? Changes)?>> write,
        GrantRevocationService grantRevocation, DateTime now, CancellationToken ct) =>
        (await RunWithNoticesAsync(name, open, write, grantRevocation, now, ct)).Outcome;
}
