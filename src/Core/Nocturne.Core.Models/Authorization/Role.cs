namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Domain model representing an authorization role.
/// Roles group Shiro-style permissions and are assigned to <see cref="Subject"/>s.
/// </summary>
/// <seealso cref="Subject"/>
/// <seealso cref="Scope"/>
public class Role
{
    public Guid Id { get; set; }

    /// <summary>
    /// Role name (e.g., "admin", "readable", "api")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description/notes
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Shiro-style permissions assigned to this role
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Whether this is a system-defined role (cannot be deleted)
    /// </summary>
    public bool IsSystemRole { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
