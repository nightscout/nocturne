using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Reads the changes to what a tenant's members can read: the roles and direct permissions they were
/// given or lost, their 24-hour history limit, and the permissions a role itself grants. Login,
/// token and credential events are never part of it, and neither are the changes to a subject's
/// global roles.
/// </summary>
public interface IMemberHistoryReader
{
    /// <summary>
    /// A page of the changes recorded for <paramref name="tenantId"/>, newest first. The tenant must come
    /// from the caller's resolved tenant: the audit table is not tenant-scoped, so this argument is the
    /// only isolation.
    /// </summary>
    /// <param name="memberSubjectId">
    /// Narrows the page to the changes made to one member. A change to a role's permissions names no
    /// member, so it never appears in a narrowed page.
    /// </param>
    Task<PaginatedResponse<MemberHistoryEntry>> GetAsync(
        Guid tenantId, Guid? memberSubjectId, int limit, int offset, CancellationToken ct = default);
}
