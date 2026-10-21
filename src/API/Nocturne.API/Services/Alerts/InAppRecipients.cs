using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Who an <c>in_app</c> alert channel may name. A notification is listed only to the subject it
/// names and only within its own tenant, so a channel naming anyone else reaches no one.
/// </summary>
internal static class InAppRecipients
{
    /// <summary>
    /// The subject <paramref name="destination"/> names, when it is an active member of
    /// <paramref name="tenantId"/> who is a person rather than a system subject; otherwise null.
    /// </summary>
    public static async Task<Guid?> ResolveAsync(
        NocturneDbContext db, Guid tenantId, string? destination, CancellationToken ct)
    {
        if (!Guid.TryParse(destination, out var subjectId))
        {
            return null;
        }

        var isMember = await db.TenantMembers
            .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId)
            .Join(db.Subjects, m => m.SubjectId, s => s.Id, (_, s) => s)
            .AnyAsync(s => s.IsActive && !s.IsSystemSubject, ct);

        return isMember ? subjectId : null;
    }
}
