using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores the global role catalogue: the Nightscout-style roles a subject is assigned through
/// <see cref="ISubjectRepository.AssignRoleAsync"/>. A tenant's own roles are on
/// <c>ITenantMembershipRepository</c>; these have no tenant.
/// </summary>
/// <remarks>
/// <c>roles</c> is a platform table with no policy, so every method runs as the scope's reader;
/// a migration job resolves its repository in the job's own scope.
/// </remarks>
public interface IRoleRepository
{
    /// <summary>Every role, by name.</summary>
    Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct);

    /// <summary>The role with <paramref name="roleId"/>; null when there is none.</summary>
    Task<Role?> GetAsync(Guid roleId, CancellationToken ct);

    /// <summary>The role named <paramref name="name"/>; null when there is none.</summary>
    Task<Role?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>Stores a role and returns it.</summary>
    Task<Role> CreateAsync(NewRole role, CancellationToken ct);

    /// <summary>Replaces the role's name, description and permissions; null when there is none.</summary>
    Task<Role?> UpdateAsync(Guid roleId, string name, string? description, IReadOnlyList<string> permissions, CancellationToken ct);

    /// <summary>
    /// Deletes the role together with every subject's assignment of it, in one save, and returns
    /// how many assignments went with it; null when there is no such role.
    /// </summary>
    Task<int?> DeleteAsync(Guid roleId, CancellationToken ct);

    /// <summary>When any role was last updated; null when there is none.</summary>
    Task<DateTime?> LastModifiedAsync(CancellationToken ct);
}
