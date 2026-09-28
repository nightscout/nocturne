namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for auth/identity entities carrying created_at / updated_at timestamps.
/// NocturneDbContext stamps <see cref="IEntityCreated.CreatedAt"/> on insert and
/// <see cref="UpdatedAt"/> on insert and on every save that modifies another column, with the
/// same insert/modify split as <see cref="ISystemTimestamped.SysUpdatedAt"/>.
/// </summary>
public interface IEntityTimestamped : IEntityCreated
{
    /// <summary>
    /// When the record was last updated.
    /// </summary>
    DateTime UpdatedAt { get; set; }
}
