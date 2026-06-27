namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A V4 record entity that carries the canonical time-series columns the shared
/// <see cref="Repositories.V4.V4RepositoryBase{TModel,TEntity}"/> filters, orders, and watermarks
/// on. Extends <see cref="IV4Entity"/> (Id, LegacyId, TenantId, DeletedAt) with the domain
/// timestamp, data source, and device. Span-shaped types (e.g. TempBasal, which keys on
/// StartTimestamp) deliberately do NOT implement this and stay off the shared base.
/// </summary>
public interface IV4TimeSeriesEntity : IV4Entity
{
    DateTime Timestamp { get; set; }
    string? DataSource { get; set; }
    string? Device { get; set; }
}
