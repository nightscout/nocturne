using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Health;

/// <summary>
/// Domain service for step count record operations. Inherits standard CRUD, SignalR broadcasting,
/// and document-processing behaviour from <see cref="TimeSeriesEntityService{TModel,TEntity}"/>.
/// </summary>
/// <seealso cref="IStepCountService"/>
/// <seealso cref="TimeSeriesEntityService{TModel,TEntity}"/>
/// <param name="documentProcessingService">Service that applies field processing before save.</param>
/// <param name="signalRBroadcastService">Service used to broadcast entity changes over SignalR.</param>
/// <param name="logger">Logger instance for this service.</param>
public class StepCountService(
    INocturneDbContextFactory contexts,
    IDocumentProcessingService documentProcessingService,
    ISignalRBroadcastService signalRBroadcastService,
    ILogger<StepCountService> logger
)
    : TimeSeriesEntityService<StepCount, StepCountEntity>(
        contexts, documentProcessingService, signalRBroadcastService, logger),
        IStepCountService
{
    protected override DbSet<StepCountEntity> EntitySet(NocturneDbContext db) => db.StepCounts;
    protected override string CollectionName => "stepcount";
    protected override string EntityTypeName => "step count";

    protected override StepCount ToDomainModel(StepCountEntity entity) =>
        StepCountMapper.ToDomainModel(entity);

    protected override StepCountEntity ToEntity(StepCount model) =>
        StepCountMapper.ToEntity(model);

    protected override void UpdateEntity(StepCountEntity entity, StepCount model) =>
        StepCountMapper.UpdateEntity(entity, model);

    public Task<IEnumerable<StepCount>> GetStepCountsAsync(
        int count = 10,
        int skip = 0,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default
    ) => GetNewestFirstAsync(count, skip, from, to, cancellationToken);

    public Task<IEnumerable<StepCount>> GetStepCountsByDateRangeAsync(
        DateTime from,
        DateTime to,
        int? count = null,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => GetByDateRangeAsync(from, to, count, skip, cancellationToken);

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
