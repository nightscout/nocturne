namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for entities carrying system-managed creation and update timestamps
/// (sys_created_at / sys_updated_at). NocturneDbContext stamps
/// <see cref="ISystemCreated.SysCreatedAt"/> on insert and <see cref="SysUpdatedAt"/>
/// on every save.
/// </summary>
public interface ISystemTimestamped : ISystemCreated
{
    /// <summary>
    /// System tracking: when the record was last updated.
    /// </summary>
    DateTime SysUpdatedAt { get; set; }
}
