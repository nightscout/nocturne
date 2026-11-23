using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Membership lookups for the authentication hot path, SignalR and OIDC, where no ambient tenant
/// is resolved: each is answered by <see cref="ITenantMembershipRepository"/> under the pin its
/// contract names, the tenant asked about or, for the one cross-tenant read, the subject. A
/// membership whose end date has passed reads as none.
/// </summary>
/// <seealso cref="ITenantMemberService"/>
public class TenantMemberService(ITenantMembershipRepository members, TimeProvider time) : ITenantMemberService
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public Task<bool> IsMemberAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default) =>
        members.IsMemberAsync(tenantId, subjectId, Now, ct);

    public Task<bool> IsDirectMemberAsync(Guid subjectId, Guid tenantId, CancellationToken ct) =>
        members.IsDirectMemberAsync(tenantId, subjectId, Now, ct);

    public async Task<List<Guid>> GetTenantIdsForSubjectAsync(Guid subjectId, CancellationToken ct = default) =>
        [.. await members.ListTenantIdsOfSubjectAsync(subjectId, ct)];

    public async Task<List<string>> GetMemberRoleNamesAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default) =>
        [.. await members.GetMemberRoleNamesAsync(tenantId, subjectId, Now, ct)];

    public Task<TenantMemberAccess?> GetMemberAccessAsync(
        Guid subjectId, Guid tenantId, CancellationToken ct = default) =>
        members.GetMemberAccessAsync(tenantId, subjectId, Now, ct);
}
