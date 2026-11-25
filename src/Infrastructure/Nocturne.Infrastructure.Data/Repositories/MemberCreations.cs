using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Keeps what a member made from outlasting their access. A write that lowers a membership's end, or
/// gives it one, calls <see cref="CapAsync"/> at that end on the unit that commits it; the reason
/// projection (<see cref="Services.MembershipReasons"/>) reports the end it lowered. A new kind of
/// thing a member can make is capped here. A lapse never moves an end
/// (<see cref="Core.Models.Identity.MembershipSum"/>), so nothing caps on one.
/// </summary>
/// <remarks>
/// It caps the guest links and device credentials the member made
/// (<see cref="OAuthGrantRepository.CapGrantsCreatedByAsync"/>, one audit row each) and the alert
/// channels they added (<see cref="Core.Models.AlertRuleChannelSnapshot.StopsAt"/>), and the end their
/// live Nightscout imports stamp on the tokens they have still to adopt
/// (<see cref="Entities.MigrationRunEntity.StarterAccessEndsAt"/>). A cap only
/// lowers: what already ends sooner keeps its end, so a later end never lengthens anything.
/// The member invites they made are not capped: a membership's end is read at accept
/// (<see cref="MemberInviteRepository.TryClaimUseAsync"/>), and only removal revokes them, with an
/// unstage when the removal is refused, so removal does that itself.
/// </remarks>
public static class MemberCreations
{
    /// <summary>
    /// Stages on <paramref name="unit"/>, without saving, the cap at <paramref name="end"/> of
    /// everything <paramref name="subjectId"/> made: the grants live at <paramref name="now"/>, every
    /// alert channel that stops later or never, and every live import they started. The unit
    /// must be pinned to the membership's tenant, and a revocation already staged on it is respected.
    /// </summary>
    /// <returns>The grants it lowered, for the caller to evict their sessions once the unit commits.</returns>
    public static async Task<IReadOnlyList<OAuthGrantRecord>> CapAsync(
        DbUnitOfWork unit, Guid subjectId, DateTime end, DateTime now, CancellationToken ct)
    {
        var lowered = await new OAuthGrantRepository(unit).CapGrantsCreatedByAsync(subjectId, end, now, ct);
        await CapAlertChannelsAsync(unit.Context, subjectId, end, ct);
        await CapImportsAsync(unit.Context, subjectId, end, ct);
        return lowered;
    }

    private static async Task CapImportsAsync(NocturneDbContext db, Guid subjectId, DateTime end, CancellationToken ct)
    {
        var tenantId = db.TenantId;
        var runs = await db.MigrationRuns
            .Where(r => r.TenantId == tenantId
                        && r.StartedBySubjectId == subjectId
                        && r.CompletedAt == null
                        && (r.StarterAccessEndsAt == null || r.StarterAccessEndsAt > end))
            .ToListAsync(ct);
        foreach (var run in runs)
            run.StarterAccessEndsAt = end;
    }

    private static async Task CapAlertChannelsAsync(NocturneDbContext db, Guid subjectId, DateTime end, CancellationToken ct)
    {
        var channels = await db.AlertRuleChannels
            .Where(c => c.AddedBySubjectId == subjectId && (c.StopsAt == null || c.StopsAt > end))
            .ToListAsync(ct);
        foreach (var channel in channels)
            channel.StopsAt = end;
    }
}
