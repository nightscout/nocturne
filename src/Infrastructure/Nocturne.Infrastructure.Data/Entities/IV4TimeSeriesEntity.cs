namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A V4 record entity that carries the canonical time-series columns the shared
/// <see cref="Repositories.V4.V4RepositoryBase{TModel,TEntity}"/> filters, orders, and watermarks
/// on. Extends <see cref="IV4Entity"/> (Id, LegacyId, TenantId, DeletedAt) and
/// <see cref="ISourcedEntity"/> (data source, device) with the domain timestamp of
/// <see cref="IObservationTimestamped"/>. Span-shaped types
/// (e.g. TempBasal, which keys on StartTimestamp) deliberately do NOT implement this and stay off
/// the shared base.
/// </summary>
/// <remarks><inheritdoc cref="IOriginalIdentified" path="/remarks"/></remarks>
public interface IV4TimeSeriesEntity : IV4Entity, ISourcedEntity, IObservationTimestamped;
