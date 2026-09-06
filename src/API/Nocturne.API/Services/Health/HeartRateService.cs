using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Health;

/// <summary>
/// Domain service for heart rate record operations. Inherits standard CRUD, SignalR broadcasting,
/// and document-processing behaviour from <see cref="TimeSeriesEntityService{TModel,TEntity}"/>.
/// </summary>
/// <seealso cref="IHeartRateService"/>
/// <seealso cref="TimeSeriesEntityService{TModel,TEntity}"/>
/// <param name="dbContext">EF Core context providing access to the heart rates table.</param>
/// <param name="documentProcessingService">Service that applies field processing before save.</param>
/// <param name="signalRBroadcastService">Service used to broadcast entity changes over SignalR.</param>
/// <param name="logger">Logger instance for this service.</param>
public class HeartRateService(
    NocturneDbContext dbContext,
    IDocumentProcessingService documentProcessingService,
    ISignalRBroadcastService signalRBroadcastService,
    ILogger<HeartRateService> logger
)
    : TimeSeriesEntityService<HeartRate, HeartRateEntity>(
        dbContext, documentProcessingService, signalRBroadcastService, logger),
        IHeartRateService
{
    protected override DbSet<HeartRateEntity> EntitySet => DbContext.HeartRates;
    protected override string CollectionName => "heartrate";
    protected override string EntityTypeName => "heart rate";

    protected override HeartRate ToDomainModel(HeartRateEntity entity) =>
        HeartRateMapper.ToDomainModel(entity);

    protected override HeartRateEntity ToEntity(HeartRate model) =>
        HeartRateMapper.ToEntity(model);

    protected override void UpdateEntity(HeartRateEntity entity, HeartRate model) =>
        HeartRateMapper.UpdateEntity(entity, model);

    public Task<IEnumerable<HeartRate>> GetHeartRatesAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => GetAllAsync(count, skip, cancellationToken);

    public Task<IEnumerable<HeartRate>> GetHeartRatesByDateRangeAsync(
        DateTime from,
        DateTime to,
        int? count = null,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => GetByDateRangeAsync(from, to, count, skip, cancellationToken);

    public Task<HeartRate?> GetHeartRateByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => GetByIdAsync(id, cancellationToken);

    public Task<IEnumerable<HeartRate>> CreateHeartRatesAsync(
        IEnumerable<HeartRate> heartRates,
        CancellationToken cancellationToken = default
    ) => CreateManyAsync(heartRates, cancellationToken);

    public Task<HeartRate?> UpdateHeartRateAsync(
        string id,
        HeartRate heartRate,
        CancellationToken cancellationToken = default
    ) => UpdateOneAsync(id, heartRate, cancellationToken);

    public Task<bool> DeleteHeartRateAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => DeleteOneAsync(id, cancellationToken);
}
