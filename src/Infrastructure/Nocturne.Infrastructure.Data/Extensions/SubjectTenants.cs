using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>The tenants a subject belongs to.</summary>
public static class SubjectTenants
{
    /// <summary>
    /// The ids of the tenants <paramref name="subjectId"/> is a member of, ended memberships included.
    /// Read on a context pinned to the subject, since the answer spans tenants.
    /// </summary>
    public static IQueryable<Guid> TenantIdsOf(this IQueryable<TenantMemberEntity> members, Guid subjectId) =>
        members.Where(m => m.SubjectId == subjectId).Select(m => m.TenantId);
}
