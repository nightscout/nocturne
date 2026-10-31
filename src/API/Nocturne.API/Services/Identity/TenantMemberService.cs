using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Membership lookups for the authentication hot path, SignalR and OIDC, where no ambient tenant
/// is resolved: each is answered by <see cref="ITenantMembershipRepository"/> under the pin its
/// contract names, the tenant asked about or, for the one cross-tenant read, the subject.
/// </summary>
/// <seealso cref="ITenantMemberService"/>
public class TenantMemberService(ITenantMembershipRepository members) : ITenantMemberService
{
    public Task<bool> IsMemberAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default) =>
        members.IsMemberAsync(tenantId, subjectId, ct);

    public async Task<List<Guid>> GetTenantIdsForSubjectAsync(Guid subjectId, CancellationToken ct = default) =>
        [.. await members.ListTenantIdsOfSubjectAsync(subjectId, ct)];

    public async Task<List<string>> GetMemberRoleNamesAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default) =>
        [.. await members.GetMemberRoleNamesAsync(tenantId, subjectId, ct)];

    public Task<TenantMemberAccess?> GetMemberAccessAsync(
        Guid subjectId, Guid tenantId, CancellationToken ct = default) =>
        members.GetMemberAccessAsync(tenantId, subjectId, ct);
}
