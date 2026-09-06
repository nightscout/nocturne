using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Health;

/// <summary>
/// Domain service for body weight record operations. Inherits standard CRUD, SignalR broadcasting,
/// and document-processing behaviour from <see cref="SimpleEntityService{TModel,TEntity}"/>.
/// </summary>
/// <seealso cref="IBodyWeightService"/>
/// <seealso cref="SimpleEntityService{TModel,TEntity}"/>
public class BodyWeightService
    : SimpleEntityService<BodyWeight, BodyWeightEntity>,
        IBodyWeightService
{
    /// <param name="dbContext">EF Core context providing access to the body weights table.</param>
    /// <param name="documentProcessingService">Service that applies field processing (e.g. identifier generation) before save.</param>
    /// <param name="signalRBroadcastService">Service used to broadcast entity changes over SignalR.</param>
    /// <param name="logger">Logger instance for this service.</param>
    public BodyWeightService(
        NocturneDbContext dbContext,
        IDocumentProcessingService documentProcessingService,
        ISignalRBroadcastService signalRBroadcastService,
        ILogger<BodyWeightService> logger
    )
        : base(dbContext, documentProcessingService, signalRBroadcastService, logger) { }

    protected override DbSet<BodyWeightEntity> EntitySet => DbContext.BodyWeights;
    protected override string CollectionName => "bodyweight";
    protected override string EntityTypeName => "body weight";

    protected override BodyWeight ToDomainModel(BodyWeightEntity entity) =>
        BodyWeightMapper.ToDomainModel(entity);

    protected override BodyWeightEntity ToEntity(BodyWeight model) =>
        BodyWeightMapper.ToEntity(model);

    protected override void UpdateEntity(BodyWeightEntity entity, BodyWeight model) =>
        BodyWeightMapper.UpdateEntity(entity, model);

    /// <remarks>Body weight keys on epoch milliseconds rather than a <see cref="DateTime"/>
    /// column, which is why it stays on <see cref="SimpleEntityService{TModel,TEntity}"/> rather
    /// than <see cref="TimeSeriesEntityService{TModel,TEntity}"/>.</remarks>
    protected override IOrderedQueryable<BodyWeightEntity> OrderByTimestamp(
        IQueryable<BodyWeightEntity> query
    ) => query.OrderByDescending(b => b.Mills);

    public Task<IEnumerable<BodyWeight>> GetBodyWeightsAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => GetAllAsync(count, skip, cancellationToken);

    public Task<BodyWeight?> GetBodyWeightByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => GetByIdAsync(id, cancellationToken);

    public Task<IEnumerable<BodyWeight>> CreateBodyWeightsAsync(
        IEnumerable<BodyWeight> bodyWeights,
        CancellationToken cancellationToken = default
    ) => CreateManyAsync(bodyWeights, cancellationToken);

    public Task<BodyWeight?> UpdateBodyWeightAsync(
        string id,
        BodyWeight bodyWeight,
        CancellationToken cancellationToken = default
    ) => UpdateOneAsync(id, bodyWeight, cancellationToken);

    public Task<bool> DeleteBodyWeightAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => DeleteOneAsync(id, cancellationToken);
}
