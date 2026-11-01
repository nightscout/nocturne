using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Manages tenant-scoped roles and their permission assignments: creation, updating, deletion
/// and slug derivation, with the owner role protected and a deletion refused that would leave a
/// member holding nothing.
/// </summary>
/// <remarks>
/// Every lookup keys on the tenant AND the role id, through
/// <see cref="ITenantMembershipRepository"/>. A role id alone is not an authorization decision.
/// </remarks>
/// <seealso cref="ITenantRoleService"/>
public partial class TenantRoleService(ITenantMembershipRepository members, TimeProvider time) : ITenantRoleService
{
    public async Task<List<TenantRoleDto>> GetRolesAsync(
        Guid tenantId, IReadOnlyCollection<string> granterScopes, CancellationToken ct = default)
    {
        var roles = await members.ListRolesAsync(tenantId, ct);
        return roles.Select(role => ToDto(role, granterScopes)).ToList();
    }

    /// <inheritdoc />
    public async Task<List<TenantRoleDto>> GetAssignableRolesAsync(
        Guid tenantId, IReadOnlyCollection<string> granterScopes, CancellationToken ct = default)
    {
        var roles = await GetRolesAsync(tenantId, granterScopes, ct);
        return roles.Where(r => r.CanGrant).ToList();
    }

    public async Task<TenantRoleDto?> GetRoleByIdAsync(
        Guid tenantId, Guid roleId, IReadOnlyCollection<string> granterScopes, CancellationToken ct = default)
    {
        var role = await members.GetRoleAsync(tenantId, roleId, ct);
        return role is null ? null : ToDto(role, granterScopes);
    }

    public async Task<SaveRoleResult> CreateRoleAsync(
        Guid tenantId,
        string name,
        string? description,
        List<string> permissions,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default)
    {
        var slug = GenerateSlug(name);
        if (await RefuseSlugAsync(tenantId, slug, roleId: null, ct) is { } refusal)
            return refusal;

        try
        {
            var created = await members.CreateRoleAsync(
                new NewTenantRole(tenantId, name, slug, description, permissions, IsSystem: false), ct);
            return SaveRoleResult.Saved(ToDto(created, granterScopes));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return SaveRoleResult.Taken;
        }
    }

    public async Task<SaveRoleResult?> UpdateRoleAsync(
        Guid tenantId,
        Guid roleId,
        string name,
        string? description,
        List<string> permissions,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default)
    {
        var existing = await members.GetRoleAsync(tenantId, roleId, ct);
        if (existing is null)
            return null;

        if (existing.Slug == RoleSeeds.Owner)
            throw new InvalidOperationException("Cannot modify the owner role.");

        var slug = existing.IsSystem ? existing.Slug : GenerateSlug(name);
        if (slug != existing.Slug && await RefuseSlugAsync(tenantId, slug, roleId, ct) is { } refusal)
            return refusal;

        try
        {
            var updated = await members.UpdateRoleAsync(
                tenantId, roleId, new TenantRoleEdit(name, slug, description, permissions),
                time.GetUtcNow().UtcDateTime, ct);
            return updated is null ? null : SaveRoleResult.Saved(ToDto(updated, granterScopes));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return SaveRoleResult.Taken;
        }
    }

    /// <summary>
    /// A seed slug is refused outright, so a tenant that deleted a seed role cannot name a custom
    /// one after it; any other slug is refused only while a role other than <paramref name="roleId"/> holds it.
    /// </summary>
    private async Task<SaveRoleResult?> RefuseSlugAsync(
        Guid tenantId, string slug, Guid? roleId, CancellationToken ct)
    {
        if (RoleSeeds.Permissions.ContainsKey(slug))
            return SaveRoleResult.Reserved;

        var holder = await members.FindRoleIdBySlugAsync(tenantId, slug, ct);
        return holder is not null && holder != roleId ? SaveRoleResult.Taken : null;
    }

    public async Task<DeleteRoleResult> DeleteRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct = default)
    {
        var role = await members.GetRoleAsync(tenantId, roleId, ct);

        if (role is null)
            return new DeleteRoleResult(false, "role_not_found", "The specified role does not exist.");

        if (role.Slug == RoleSeeds.Owner)
            return new DeleteRoleResult(false, "owner_role_protected", "The owner role cannot be deleted.");

        if (role.MemberCount > 0)
        {
            var affected = (await members.ListMembersAsync(tenantId, ct))
                .Where(m => m.Roles.Any(r => r.RoleId == roleId));
            if (affected.Any(m => !m.EffectivePermissions(excludingRoleId: roleId).Any()))
            {
                return new DeleteRoleResult(
                    false,
                    "members_would_lose_all_permissions",
                    "Deleting this role would leave one or more members with no permissions.");
            }
        }

        await members.DeleteRoleAsync(tenantId, roleId, ct);
        return new DeleteRoleResult(true, null, null);
    }

    /// <summary>Seeds the tenant's default roles, skipping slugs that already exist.</summary>
    /// <remarks>
    /// Every caller is a tenant-creation flow reached without a resolved tenant (first-run setup,
    /// platform admin, demo and dev provisioning), so the repository's tenant pin is what puts the
    /// rows inside the RLS reach they belong to.
    /// </remarks>
    public Task SeedRolesForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        members.SeedRolesAsync(tenantId, ct);

    /// <inheritdoc />
    public async Task<RoleGrantValidation> ValidateRoleGrantAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> roleIds,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default)
    {
        if (roleIds.Count == 0)
            return RoleGrantValidation.Valid;

        var permissionSets = await members.ListRolePermissionsAsync(tenantId, roleIds, ct);

        // Counted rather than compared by id: a duplicate id would otherwise pass a set comparison
        // while resolving fewer rows than requested.
        if (permissionSets.Count != roleIds.Distinct().Count())
        {
            return new RoleGrantValidation(
                false, RoleGrantValidation.ForeignRole, "One or more role IDs do not belong to this tenant.");
        }

        var exceeded = Scope.ValidateGrant(permissionSets.SelectMany(permissions => permissions), granterScopes);

        return exceeded is null
            ? RoleGrantValidation.Valid
            : new RoleGrantValidation(false, exceeded.Code, exceeded.Description);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Keyed on the membership id alone, which is not an authorization decision — the caller has
    /// already established that the id belongs to its tenant. A membership the context cannot
    /// reach resolves to no permissions rather than throwing.
    /// </remarks>
    public async Task<List<string>> GetEffectivePermissionsAsync(Guid memberId, CancellationToken ct = default)
    {
        var access = await members.GetMemberAccessAsync(memberId, ct);
        return access is null ? [] : [.. access.EffectivePermissions];
    }

    /// <inheritdoc />
    public async Task<List<string>> GetRolePermissionsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct = default)
    {
        if (roleIds.Count == 0)
            return [];

        var permissionSets = await members.ListRolePermissionsAsync(tenantId, roleIds, ct);
        return permissionSets.SelectMany(permissions => permissions).Distinct().ToList();
    }

    /// <summary>
    /// <c>CanGrant</c> applies the grant ceiling of <see cref="ValidateRoleGrantAsync"/> to this role alone.
    /// </summary>
    private static TenantRoleDto ToDto(TenantRoleRecord role, IReadOnlyCollection<string> granterScopes) =>
        new(
            role.Id,
            role.Name,
            role.Slug,
            role.Description,
            [.. role.Permissions],
            role.IsSystem,
            role.MemberCount,
            role.SysCreatedAt,
            Scope.ValidateGrant(role.Permissions, granterScopes) is null);

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        slug = NonAlphanumericOrDash().Replace(slug, "");
        slug = MultipleDashes().Replace(slug, "-");
        return slug.Trim('-');
    }

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex NonAlphanumericOrDash();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultipleDashes();
}
