using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Runs a <see cref="TenantMembershipRepository"/> owner-guarded change on a unit of its own pinned to
/// the tenant, saving it when it reports <see cref="OwnerGuardedChange.Changed"/> and committing it, for a
/// test of the repository's own guard. It does not sync the member's team reasons, which the API's callers
/// do in the same commit (<c>TeamReasonWrites.SyncOwnerChangeAsync</c>); a test of that goes through them.
/// </summary>
public static class OwnerGuardedWrites
{
    public static Task<DirectReasonRemoval> RemoveMemberUnlessLastOwnerAsync(
        INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute, Guid tenantId, Guid memberId,
        MembershipEndHow how, DateTime now, CancellationToken ct) =>
        OnUnitAsync(
            contexts, tenantId,
            (members, token) => members.RemoveMemberUnlessLastOwnerAsync(tenantId, memberId, how, now, token),
            removal => removal.Outcome, linkRoute, ct);

    public static Task<OwnerGuardedChange> SetAccessEndUnlessOwnerAsync(
        INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute, Guid tenantId, Guid memberId,
        DateTime? accessEndsAt, DateTime now, CancellationToken ct) =>
        OnUnitAsync(
            contexts, tenantId,
            (members, token) => members.SetAccessEndUnlessOwnerAsync(tenantId, memberId, accessEndsAt, now, token),
            outcome => outcome, linkRoute, ct);

    public static Task<OwnerGuardedChange> ReplaceRolesUnlessLastOwnerAsync(
        INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute, Guid tenantId, Guid memberId,
        IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds, DateTime now, CancellationToken ct) =>
        OnUnitAsync(
            contexts, tenantId,
            (members, token) => members.ReplaceRolesUnlessLastOwnerAsync(tenantId, memberId, removedRoleIds, addedRoleIds, now, token),
            outcome => outcome, linkRoute, ct);

    private static async Task<T> OnUnitAsync<T>(
        INocturneDbContextFactory contexts, Guid tenantId,
        Func<TenantMembershipRepository, CancellationToken, Task<T>> change, Func<T, OwnerGuardedChange> outcome,
        IAccountLinkRoute linkRoute, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, ct);
        return await unit.CommitAsync(
            async token =>
            {
                var result = await change(new TenantMembershipRepository(unit, linkRoute), token);
                if (outcome(result) == OwnerGuardedChange.Changed)
                    await unit.Context.SaveChangesAsync(token);
                return result;
            },
            ct: ct);
    }
}
