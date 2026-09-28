namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for entities carrying system-managed creation and update timestamps
/// (sys_created_at / sys_updated_at). NocturneDbContext stamps
/// <see cref="ISystemCreated.SysCreatedAt"/> on insert and <see cref="SysUpdatedAt"/>
/// on insert and on every save that modifies another column.
/// On insert the stamp is unconditional, so an update stamp assigned while constructing a row is
/// dead. On a modify it is load-bearing: a save whose only modified column is
/// <see cref="SysUpdatedAt"/> is a deliberate touch, and keeps the caller's value.
/// </summary>
public interface ISystemTimestamped : ISystemCreated
{
    /// <summary>
    /// System tracking: when the record was last updated.
    /// </summary>
    DateTime SysUpdatedAt { get; set; }
}
