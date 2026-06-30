namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A V4 record entity that carries the canonical time-series columns the shared
/// <see cref="Repositories.V4.V4RepositoryBase{TModel,TEntity}"/> filters, orders, and watermarks
/// on. Extends <see cref="IV4Entity"/> (Id, LegacyId, TenantId, DeletedAt) with the domain
/// timestamp, data source, and device. Span-shaped types (e.g. TempBasal, which keys on
/// StartTimestamp) deliberately do NOT implement this and stay off the shared base.
///
/// Implementers MUST map these three members as ordinary EF columns (plain auto-properties with a
/// <c>[Column]</c> mapping) — not explicit-interface implementations, backing-field-only, or
/// <c>[NotMapped]</c> — so the base's generic <c>ctx.Set&lt;TEntity&gt;()</c> queries translate the
/// interface-member access to SQL (the same way it already does for ITenantScoped.TenantId and
/// ISoftDeletable.DeletedAt).
/// </summary>
public interface IV4TimeSeriesEntity : IV4Entity
{
    DateTime Timestamp { get; set; }
    string? DataSource { get; set; }
    string? Device { get; set; }
}
