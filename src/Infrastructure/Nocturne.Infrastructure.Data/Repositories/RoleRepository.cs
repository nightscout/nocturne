using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IRoleRepository"/> over <see cref="NocturneDbContext"/>: one context per call,
/// saving each write. Writes are tracked reads and saves.
/// </summary>
public class RoleRepository(INocturneDbContextFactory contexts) : IRoleRepository
{
    private readonly INocturneDbContextFactory _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(ToRole).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<Role?> GetAsync(Guid roleId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct);
        return role is null ? null : ToRole(role);
    }

    /// <inheritdoc/>
    public virtual async Task<Role?> FindByNameAsync(string name, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == name, ct);
        return role is null ? null : ToRole(role);
    }

    /// <inheritdoc/>
    public virtual async Task<Role> CreateAsync(NewRole role, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = new RoleEntity
        {
            Id = Guid.CreateVersion7(),
            Name = role.Name,
            Description = role.Description,
            Permissions = [.. role.Permissions],
            IsSystemRole = role.IsSystemRole,
        };
        db.Roles.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToRole(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<Role?> UpdateAsync(
        Guid roleId, string name, string? description, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (entity is null)
            return null;

        entity.Name = name;
        entity.Description = description;
        entity.Permissions = [.. permissions];
        await db.SaveChangesAsync(ct);
        return ToRole(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<int?> DeleteAsync(Guid roleId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (entity is null)
            return null;

        var assignments = await db.SubjectRoles.Where(sr => sr.RoleId == roleId).ToListAsync(ct);
        db.SubjectRoles.RemoveRange(assignments);
        db.Roles.Remove(entity);
        await db.SaveChangesAsync(ct);
        return assignments.Count;
    }

    /// <inheritdoc/>
    public virtual async Task<DateTime?> LastModifiedAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await db.Roles.AsNoTracking()
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => (DateTime?)r.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static Role ToRole(RoleEntity role) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Description = role.Description,
        Permissions = [.. role.Permissions],
        IsSystemRole = role.IsSystemRole,
        CreatedAt = role.CreatedAt,
        UpdatedAt = role.UpdatedAt,
    };
}
