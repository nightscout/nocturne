using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Keeps the alert channels a member added from outlasting their access: a channel whose
/// destination was typed in stops at the end of the access of whoever added it
/// (<see cref="Core.Models.AlertRuleChannelSnapshot.StopsAt"/>).
/// </summary>
public static class AlertChannelStops
{
    /// <summary>
    /// Stages, on <paramref name="db"/>, a stop no later than <paramref name="at"/> on every channel
    /// <paramref name="subjectId"/> added. A channel that already stops earlier keeps its stop, so a
    /// later end never moves a stop back. <paramref name="db"/> must be pinned to the membership's tenant.
    /// </summary>
    public static async Task CapAsync(NocturneDbContext db, Guid subjectId, DateTime at, CancellationToken ct)
    {
        var channels = await db.AlertRuleChannels
            .Where(c => c.AddedBySubjectId == subjectId && (c.StopsAt == null || c.StopsAt > at))
            .ToListAsync(ct);
        foreach (var channel in channels)
            channel.StopsAt = at;
    }
}
