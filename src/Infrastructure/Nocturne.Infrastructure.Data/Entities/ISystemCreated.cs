namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for entities carrying a system-managed creation timestamp (sys_created_at).
/// NocturneDbContext stamps <see cref="SysCreatedAt"/> on insert, unconditionally: a value
/// assigned before the insert does not survive it, so a historical timestamp can only be applied
/// by a second save against the inserted row.
/// </summary>
public interface ISystemCreated
{
    /// <summary>
    /// System tracking: when the record was inserted.
    /// </summary>
    DateTime SysCreatedAt { get; set; }
}
