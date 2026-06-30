namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for entities carrying a system-managed creation timestamp (sys_created_at).
/// NocturneDbContext stamps <see cref="SysCreatedAt"/> on insert.
/// </summary>
public interface ISystemCreated
{
    /// <summary>
    /// System tracking: when the record was inserted.
    /// </summary>
    DateTime SysCreatedAt { get; set; }
}
