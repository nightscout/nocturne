using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Stops, on one tenant unit, what a subject made there: every live guest link they made, whoever the
/// link's data owner is, and every live member invite they made, each audited, and when asked, the
/// device credentials and alert channels they made (<see cref="MemberCreations"/>).
/// <see cref="MembershipRemoval"/> and <see cref="SubjectDeletion"/> both stop them through it.
/// </summary>
/// <remarks>
/// A guest link's grant belongs to its data owner, so it is found by the subject who made it. The
/// audited revocations are staged first (<see cref="StageAsync"/>) so a removal that is then refused
/// can take them back (<see cref="Unstage"/>), and the guest links are revoked before the cap
/// (<see cref="FinishAsync"/>), which respects a revocation already staged on the unit.
/// </remarks>
internal sealed class MadeByStop
{
    private readonly DbUnitOfWork _unit;
    private readonly MemberInviteRepository _invites;
    private readonly Guid _subjectId;
    private readonly DateTime _now;
    private readonly IReadOnlyList<OAuthGrantRecord> _guestLinks;
    private readonly IReadOnlyList<AuthAuditLogEntity> _auditRows;
    private readonly IReadOnlyList<Guid> _revokedInvites;

    private MadeByStop(
        DbUnitOfWork unit,
        MemberInviteRepository invites,
        Guid subjectId,
        DateTime now,
        IReadOnlyList<OAuthGrantRecord> guestLinks,
        IReadOnlyList<AuthAuditLogEntity> auditRows,
        IReadOnlyList<Guid> revokedInvites)
    {
        _unit = unit;
        _invites = invites;
        _subjectId = subjectId;
        _now = now;
        _guestLinks = guestLinks;
        _auditRows = auditRows;
        _revokedInvites = revokedInvites;
    }

    /// <summary>
    /// Stages and saves on <paramref name="unit"/>, inside its commit, the whole stop of what
    /// <paramref name="subjectId"/> made on <paramref name="tenantId"/>, device credentials and alert
    /// channels included, and returns the grants it revoked or capped for
    /// <see cref="GrantRevocationService.EvictSessions"/> once the unit commits.
    /// </summary>
    public static async Task<IReadOnlyList<OAuthGrantRecord>> StopAsync(
        DbUnitOfWork unit,
        GrantRevocationService grantRevocation,
        Guid tenantId,
        Guid subjectId,
        DateTime now,
        CancellationToken ct)
    {
        var stop = await StageAsync(unit, tenantId, subjectId, now, ct);
        var revoked = await stop.FinishAsync(grantRevocation, [], capAt: now, ct);
        await unit.Context.SaveChangesAsync(ct);
        return revoked;
    }

    /// <summary>
    /// Finds the guest links <paramref name="subjectId"/> made that are live at <paramref name="now"/>
    /// and stages their audit rows, and stages the audited revocation of the invites they made on
    /// <paramref name="tenantId"/>. Saves nothing.
    /// </summary>
    public static async Task<MadeByStop> StageAsync(
        DbUnitOfWork unit, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        var context = unit.Context;
        var grants = new OAuthGrantRepository(unit);
        var guestLinks = await grants.GetLiveGuestLinksCreatedByAsync(subjectId, now, ct);
        var before = AddedAuditRows(context);
        grants.StageMemberRemovalGuestLinkAudit(guestLinks, subjectId);
        var invites = new MemberInviteRepository(unit);
        var revokedInvites = await invites.StageRevokeLiveMadeByAsync(tenantId, subjectId, now, ct);
        var auditRows = AddedAuditRows(context).Except(before).ToList();
        return new MadeByStop(unit, invites, subjectId, now, guestLinks, auditRows, revokedInvites);
    }

    /// <summary>Takes back everything <see cref="StageAsync"/> staged.</summary>
    public void Unstage()
    {
        foreach (var row in _auditRows)
            _unit.Context.Entry(row).State = EntityState.Detached;
        _invites.UnstageRevokeLiveMadeBy(_revokedInvites.ToHashSet());
    }

    /// <summary>
    /// Stages the revocation of <paramref name="alsoRevoke"/> and the guest links with every
    /// consequence, then, when <paramref name="capAt"/> is set, the cap of what else the subject made
    /// at that instant. Saves nothing.
    /// </summary>
    /// <returns>
    /// The grants it revoked and the grants the cap lowered, for
    /// <see cref="GrantRevocationService.EvictSessions"/> once the unit commits.
    /// </returns>
    public async Task<IReadOnlyList<OAuthGrantRecord>> FinishAsync(
        GrantRevocationService grantRevocation,
        IReadOnlyCollection<OAuthGrantRecord> alsoRevoke,
        DateTime? capAt,
        CancellationToken ct)
    {
        var revoked = alsoRevoke.Concat(_guestLinks.ExceptBy(alsoRevoke.Select(g => g.Id), g => g.Id)).ToList();
        await grantRevocation.StageAsync(_unit, revoked, _now, ct);
        IReadOnlyList<OAuthGrantRecord> capped = capAt is { } end
            ? await MemberCreations.CapAsync(_unit, _subjectId, end, _now, ct)
            : [];
        return [.. revoked, .. capped];
    }

    private static List<AuthAuditLogEntity> AddedAuditRows(NocturneDbContext context) =>
        context.ChangeTracker.Entries<AuthAuditLogEntity>()
            .Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
}
