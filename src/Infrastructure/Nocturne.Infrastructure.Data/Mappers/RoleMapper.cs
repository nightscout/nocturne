using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

public static class RoleMapper
{
    /// <summary>
    /// A stored jsonb <c>null</c> in <c>permissions</c> materialises as a null list, so it reads as no permissions.
    /// </summary>
    public static Role ToDomainModel(RoleEntity role) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Description = role.Description,
        Permissions = role.Permissions is null ? [] : [.. role.Permissions],
        IsSystemRole = role.IsSystemRole,
        CreatedAt = role.CreatedAt,
        UpdatedAt = role.UpdatedAt,
    };
}
