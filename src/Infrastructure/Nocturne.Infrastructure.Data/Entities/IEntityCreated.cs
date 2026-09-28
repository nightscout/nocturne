namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for auth/identity entities carrying a created_at timestamp set on insert.
/// Separate from <see cref="ISystemCreated"/> because these tables use the
/// created_at / updated_at naming convention rather than sys_*.
/// NocturneDbContext stamps <see cref="CreatedAt"/> on insert, unconditionally: a value assigned
/// before the insert does not survive it, so a historical timestamp can only be applied by a
/// second save against the inserted row.
/// </summary>
public interface IEntityCreated
{
    /// <summary>
    /// When the record was created.
    /// </summary>
    DateTime CreatedAt { get; set; }
}
