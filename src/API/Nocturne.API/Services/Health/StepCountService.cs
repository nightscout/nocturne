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
/// Domain service for step count record operations. Inherits standard CRUD, SignalR broadcasting,
/// and document-processing behaviour from <see cref="SimpleEntityService{TModel,TEntity}"/>.
/// </summary>
/// <seealso cref="IStepCountService"/>
/// <seealso cref="SimpleEntityService{TModel,TEntity}"/>
public class StepCountService
    : SimpleEntityService<StepCount, StepCountEntity>,
        IStepCountService
{
    /// <param name="dbContext">EF Core context providing access to the step counts table.</param>
    /// <param name="documentProcessingService">Service that applies field processing before save.</param>
    /// <param name="signalRBroadcastService">Service used to broadcast entity changes over SignalR.</param>
    /// <param name="logger">Logger instance for this service.</param>
    public StepCountService(
        NocturneDbContext dbContext,
        IDocumentProcessingService documentProcessingService,
        ISignalRBroadcastService signalRBroadcastService,
        ILogger<StepCountService> logger
    )
        : base(dbContext, documentProcessingService, signalRBroadcastService, logger) { }

    protected override DbSet<StepCountEntity> EntitySet => DbContext.StepCounts;
    protected override string CollectionName => "stepcount";
    protected override string EntityTypeName => "step count";

    protected override StepCount ToDomainModel(StepCountEntity entity) =>
        StepCountMapper.ToDomainModel(entity);

    protected override StepCountEntity ToEntity(StepCount model) =>
        StepCountMapper.ToEntity(model);

    protected override void UpdateEntity(StepCountEntity entity, StepCount model) =>
        StepCountMapper.UpdateEntity(entity, model);

    protected override IOrderedQueryable<StepCountEntity> OrderByTimestamp(
        IQueryable<StepCountEntity> query
    ) => query.OrderByDescending(s => s.Timestamp);

    protected override Task<StepCountEntity?> FindByIdAsync(
        string id,
        CancellationToken cancellationToken
    ) =>
        Guid.TryParse(id, out var guid)
            ? DbContext.StepCounts.FirstOrDefaultAsync(s => s.Id == guid, cancellationToken)
            : DbContext.StepCounts.FirstOrDefaultAsync(
                s => s.OriginalId == id,
                cancellationToken
            );

    public Task<IEnumerable<StepCount>> GetStepCountsAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => GetAllAsync(count, skip, cancellationToken);

    public async Task<IEnumerable<StepCount>> GetStepCountsByDateRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    )
    {
        var entities = await EntitySet
            .Where(s => s.Timestamp >= from && s.Timestamp < to)
            .OrderBy(s => s.Timestamp)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDomainModel);
    }

    public Task<StepCount?> GetStepCountByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => GetByIdAsync(id, cancellationToken);

    public Task<IEnumerable<StepCount>> CreateStepCountsAsync(
        IEnumerable<StepCount> stepCounts,
        CancellationToken cancellationToken = default
    ) => CreateManyAsync(stepCounts, cancellationToken);

    public Task<StepCount?> UpdateStepCountAsync(
        string id,
        StepCount stepCount,
        CancellationToken cancellationToken = default
    ) => UpdateOneAsync(id, stepCount, cancellationToken);

    public Task<bool> DeleteStepCountAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => DeleteOneAsync(id, cancellationToken);
}
