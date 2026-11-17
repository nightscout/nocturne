using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Who an <c>in_app</c> alert channel may name: a person who is a member of the rule's tenant
/// (<see cref="ITenantMembershipRepository.ListPeopleAsync"/>) and whose roles or direct
/// permissions grant any access, which a member given the denied role does not. A notification
/// is listed only to the subject it names and only within its own tenant, so a channel naming
/// anyone else reaches no one.
/// </summary>
internal static class InAppRecipients
{
    /// <summary>The subject <paramref name="destination"/> names, when it may receive; otherwise null.</summary>
    public static async Task<Guid?> ResolveAsync(
        ITenantMembershipRepository members, Guid tenantId, string? destination, CancellationToken ct)
    {
        if (!Guid.TryParse(destination, out var subjectId))
        {
            return null;
        }

        var memberships = await members.ListPeopleAsync(tenantId, ct);

        return memberships.Any(m => m.SubjectId == subjectId && MayReceive(m)) ? subjectId : null;
    }

    /// <summary>Every subject that may receive, with the name the site knows them by.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ListAsync(
        ITenantMembershipRepository members, Guid tenantId, CancellationToken ct)
    {
        var memberships = await members.ListPeopleAsync(tenantId, ct);

        var names = new Dictionary<Guid, string>();
        foreach (var membership in memberships.Where(MayReceive))
            names.TryAdd(membership.SubjectId, membership.Label ?? membership.SubjectName ?? string.Empty);
        return names;
    }

    /// <summary>The tenant's longest-standing owner who may receive, or null when it has none.</summary>
    public static async Task<Guid?> EarliestOwnerAsync(
        ITenantMembershipRepository members, Guid tenantId, CancellationToken ct)
    {
        var owners = await members.ListOwnersAsync(tenantId, ct);
        return owners.FirstOrDefault(MayReceive)?.SubjectId;
    }

    private static bool MayReceive(TenantMemberRecord membership) =>
        membership.EffectivePermissions().Any();
}
