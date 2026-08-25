using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Manages tenant-scoped roles and their permission assignments. Supports creation, updating,
/// deletion, and slug validation of <see cref="TenantRoleDto"/> records.
/// </summary>
/// <remarks>
/// <c>tenant_roles</c> carries no global query filter and no Row Level Security policy (it sits
/// in the identity cluster alongside <c>tenant_members</c>, which auth resolution reads before a
/// tenant context exists), so every lookup here keys on the tenant AND the role ID. A role ID
/// alone is not an authorization decision.
/// </remarks>
/// <seealso cref="ITenantRoleService"/>
public partial class TenantRoleService(
    NocturneDbContext context,
    IDbContextFactory<NocturneDbContext> contextFactory) : ITenantRoleService
{
    public async Task<List<TenantRoleDto>> GetRolesAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await context.TenantRoles
            .Where(r => r.TenantId == tenantId)
            .Select(r => new TenantRoleDto(
                r.Id,
                r.Name,
                r.Slug,
                r.Description,
                r.Permissions,
                r.IsSystem,
                r.MemberRoles.Count,
                r.SysCreatedAt
            ))
            .ToListAsync(ct);
    }

    public async Task<TenantRoleDto?> GetRoleByIdAsync(Guid tenantId, Guid roleId, CancellationToken ct = default)
    {
        return await context.TenantRoles
            .Where(r => r.Id == roleId && r.TenantId == tenantId)
            .Select(r => new TenantRoleDto(
                r.Id,
                r.Name,
                r.Slug,
                r.Description,
                r.Permissions,
                r.IsSystem,
                r.MemberRoles.Count,
                r.SysCreatedAt
            ))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<TenantRoleDto> CreateRoleAsync(
        Guid tenantId,
        string name,
        string? description,
        List<string> permissions,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            Slug = GenerateSlug(name),
            Description = description,
            Permissions = permissions,
            IsSystem = false,
            SysCreatedAt = now,
            SysUpdatedAt = now,
        };

        context.TenantRoles.Add(entity);
        await context.SaveChangesAsync(ct);

        return new TenantRoleDto(
            entity.Id,
            entity.Name,
            entity.Slug,
            entity.Description,
            entity.Permissions,
            entity.IsSystem,
            0,
            entity.SysCreatedAt
        );
    }

    public async Task<TenantRoleDto?> UpdateRoleAsync(
        Guid tenantId,
        Guid roleId,
        string name,
        string? description,
        List<string> permissions,
        CancellationToken ct = default)
    {
        var entity = await context.TenantRoles
            .Include(r => r.MemberRoles)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == tenantId, ct);

        if (entity is null)
            return null;

        if (entity.Slug == RoleSeeds.Owner)
            throw new InvalidOperationException("Cannot modify the owner role.");

        entity.Name = name;
        entity.Slug = GenerateSlug(name);
        entity.Description = description;
        entity.Permissions = permissions;
        entity.SysUpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);

        return new TenantRoleDto(
            entity.Id,
            entity.Name,
            entity.Slug,
            entity.Description,
            entity.Permissions,
            entity.IsSystem,
            entity.MemberRoles.Count,
            entity.SysCreatedAt
        );
    }

    public async Task<DeleteRoleResult> DeleteRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct = default)
    {
        var role = await context.TenantRoles
            .Include(r => r.MemberRoles)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == tenantId, ct);

        if (role is null)
            return new DeleteRoleResult(false, "role_not_found", "The specified role does not exist.");

        if (role.Slug == RoleSeeds.Owner)
            return new DeleteRoleResult(false, "owner_role_protected", "The owner role cannot be deleted.");

        // Check if any member would lose all permissions
        var affectedMemberIds = role.MemberRoles.Select(mr => mr.TenantMemberId).ToList();
        if (affectedMemberIds.Count > 0)
        {
            var affectedMembers = await context.TenantMembers
                .Include(m => m.MemberRoles)
                    .ThenInclude(mr => mr.TenantRole)
                .Where(m => affectedMemberIds.Contains(m.Id))
                .ToListAsync(ct);

            foreach (var member in affectedMembers)
            {
                // Compute remaining permissions without this role
                var remainingPermissions = member.MemberRoles
                    .Where(mr => mr.TenantRoleId != roleId)
                    .SelectMany(mr => mr.TenantRole.Permissions)
                    .Union(member.DirectPermissions ?? [])
                    .ToList();

                if (remainingPermissions.Count == 0)
                {
                    return new DeleteRoleResult(
                        false,
                        "members_would_lose_all_permissions",
                        "Deleting this role would leave one or more members with no permissions.");
                }
            }
        }

        // Remove member-role associations then the role itself
        context.TenantMemberRoles.RemoveRange(role.MemberRoles);
        context.TenantRoles.Remove(role);
        await context.SaveChangesAsync(ct);

        return new DeleteRoleResult(true, null, null);
    }

    /// <summary>
    /// Seeds the tenant's default roles, skipping slugs that already exist.
    /// </summary>
    /// <remarks>
    /// Runs on its own context pinned to <paramref name="tenantId"/>, not the injected
    /// request-scoped one. Every caller is a tenant-creation flow reached without a resolved
    /// tenant (first-run setup, platform admin, demo and dev provisioning), so the scoped context
    /// is pinned to no tenant and would seed outside the RLS reach the rows belong to.
    /// </remarks>
    /// <param name="tenantId">The tenant to seed roles for.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task SeedRolesForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var seedContext = await contextFactory.CreateTenantPinnedContextAsync(tenantId, ct);

        var existingSlugs = await seedContext.TenantRoles
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.Slug)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var (slug, permissions) in RoleSeeds.Permissions)
        {
            if (existingSlugs.Contains(slug))
                continue;

            var name = RoleSeeds.DisplayNames[slug];
            seedContext.TenantRoles.Add(new TenantRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = name,
                Slug = slug,
                Description = null,
                Permissions = new List<string>(permissions),
                IsSystem = true,
                SysCreatedAt = now,
                SysUpdatedAt = now,
            });
        }

        await seedContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<RoleGrantValidation> ValidateRoleGrantAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> roleIds,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default)
    {
        if (roleIds.Count == 0)
            return RoleGrantValidation.Valid;

        var permissionSets = await context.TenantRoles
            .Where(r => r.TenantId == tenantId && roleIds.Contains(r.Id))
            .Select(r => r.Permissions)
            .ToListAsync(ct);

        // Counted rather than compared by id: a duplicate id would otherwise pass a set comparison
        // while resolving fewer rows than requested.
        if (permissionSets.Count != roleIds.Distinct().Count())
        {
            return new RoleGrantValidation(
                false, RoleGrantValidation.ForeignRole, "One or more role IDs do not belong to this tenant.");
        }

        var exceeded = Scope.ValidateGrant(
            permissionSets.SelectMany(permissions => permissions), granterScopes);

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
        var member = await context.TenantMembers
            .Include(m => m.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .FirstOrDefaultAsync(m => m.Id == memberId, ct);

        if (member is null)
            return [];

        var rolePermissions = member.MemberRoles
            .SelectMany(mr => mr.TenantRole.Permissions);

        var directPermissions = member.DirectPermissions ?? [];

        return rolePermissions
            .Union(directPermissions)
            .ToList();
    }

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
