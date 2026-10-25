using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>Who is recording that they saw an alarm. <see cref="Label"/> is kept as it was then.</summary>
public sealed record AlertReceiptActor(AlertReceiptActorKind Kind, Guid Id, string? Label = null)
{
    /// <summary>
    /// The actor a request records as, from its credential and never from its body: the public link,
    /// a guest link's grant, or the member's own subject. Null when the caller is none of these.
    /// </summary>
    /// <remarks>
    /// A guest session carries the data owner in <see cref="AuthContext.ActingAsSubjectId"/>, so a
    /// guest is named by its grant, never by <see cref="AuthContext.EffectiveSubjectId"/>: that
    /// would record the owner as having seen it.
    /// </remarks>
    public static AlertReceiptActor? Of(HttpContext context)
    {
        var auth = context.GetAuthContext();
        if (context.IsShareAccess())
            return auth?.SubjectId is { } publicSubject ? new(AlertReceiptActorKind.PublicLink, publicSubject) : null;

        if (auth is not { IsAuthenticated: true })
            return null;

        if (auth.AuthType == AuthType.Guest)
            return context.GetGrantId() is { } grant ? new(AlertReceiptActorKind.Guest, grant) : null;

        return auth.SubjectId is { } subject ? new(AlertReceiptActorKind.Member, subject) : null;
    }
}

/// <summary>A receipt as an alert read returns it.</summary>
public sealed record AlertReceipt(
    AlertReceiptActorKind ActorKind, Guid ActorId, string? ActorLabel, DateTime SeenAt);

/// <summary>
/// Records that someone saw an alarm, and reads those records back. A receipt changes nothing about
/// the alert: it neither acknowledges nor mutes, for the viewer or anyone else.
/// </summary>
public sealed class AlertReceiptService(INocturneDbContextFactory contexts)
{
    /// <summary>
    /// Records that <paramref name="actor"/> saw the excursion. Recording it twice keeps the first.
    /// False when no open excursion has that id.
    /// </summary>
    public async Task<bool> RecordAsync(Guid excursionId, AlertReceiptActor actor, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);

        var excursion = await db.AlertExcursions
            .AsNoTracking()
            .Where(e => e.Id == excursionId && e.EndedAt == null)
            .Select(e => new { e.Id, e.TenantId })
            .FirstOrDefaultAsync(ct);
        if (excursion is null)
            return false;

        var exists = await db.AlertExcursionReceipts.AnyAsync(r =>
            r.AlertExcursionId == excursionId && r.ActorKind == actor.Kind && r.ActorId == actor.Id, ct);
        if (exists)
            return true;

        var label = actor.Kind == AlertReceiptActorKind.Guest
            ? await db.OAuthGrants.Where(g => g.Id == actor.Id).Select(g => g.Label).FirstOrDefaultAsync(ct)
            : actor.Label;
        var seenAt = DateTime.UtcNow;

        db.AlertExcursionReceipts.Add(new AlertExcursionReceiptEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = excursion.TenantId,
            AlertExcursionId = excursionId,
            ActorKind = actor.Kind,
            ActorId = actor.Id,
            ActorLabel = label,
            SeenAt = seenAt,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // A concurrent press by the same viewer won the insert; the receipt stands either way.
            return true;
        }

        return true;
    }

    /// <summary>
    /// The receipts of <paramref name="excursionIds"/>, a member's named from their subject. With
    /// <paramref name="onlyFor"/>, only that actor's.
    /// </summary>
    public static async Task<ILookup<Guid, AlertReceipt>> ReadAsync(
        NocturneDbContext db, IReadOnlyCollection<Guid> excursionIds, AlertReceiptActor? onlyFor,
        CancellationToken ct)
    {
        var query = db.AlertExcursionReceipts.AsNoTracking()
            .Where(r => excursionIds.Contains(r.AlertExcursionId));
        if (onlyFor is not null)
            query = query.Where(r => r.ActorKind == onlyFor.Kind && r.ActorId == onlyFor.Id);

        var rows = await query.OrderBy(r => r.SeenAt).ToListAsync(ct);

        var memberIds = rows.Where(r => r.ActorKind == AlertReceiptActorKind.Member)
            .Select(r => r.ActorId).Distinct().ToList();
        var names = memberIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Subjects.AsNoTracking()
                .Where(s => memberIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        return rows.ToLookup(
            r => r.AlertExcursionId,
            r => new AlertReceipt(
                r.ActorKind,
                r.ActorId,
                r.ActorKind == AlertReceiptActorKind.Member ? names.GetValueOrDefault(r.ActorId) : r.ActorLabel,
                r.SeenAt));
    }
}
