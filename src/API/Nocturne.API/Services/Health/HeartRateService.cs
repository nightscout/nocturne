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

    public async Task<IEnumerable<HeartRate>> GetHeartRateMinuteAveragesByDateRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    )
    {
        // Group in PostgreSQL so a dense wearable stream does not cross the API boundary as
        // hundreds of thousands of points. The source table remains untouched and the normal
        // tenant/soft-delete filters on EntitySet still apply.
        var rangeStart = from;
        var rangeEnd = to;
        var buckets = await (
            from row in EntitySet.AsNoTracking()
            where row.Timestamp >= rangeStart && row.Timestamp < rangeEnd
            let utcTimestamp = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(row.Timestamp, "UTC")
            group row by new
            {
                utcTimestamp.Year,
                utcTimestamp.Month,
                utcTimestamp.Day,
                utcTimestamp.Hour,
                utcTimestamp.Minute,
            }
            into minute
            orderby minute.Key.Year, minute.Key.Month, minute.Key.Day,
                minute.Key.Hour, minute.Key.Minute
            select new
            {
                minute.Key.Year,
                minute.Key.Month,
                minute.Key.Day,
                minute.Key.Hour,
                minute.Key.Minute,
                AverageBpm = minute.Average(row => (double)row.Bpm),
            }).ToListAsync(cancellationToken);

        return buckets.Select(bucket => new HeartRate
        {
            Timestamp = new DateTime(bucket.Year, bucket.Month, bucket.Day,
                bucket.Hour, bucket.Minute,
                0,
                DateTimeKind.Utc),
            Bpm = (int)Math.Round(bucket.AverageBpm, MidpointRounding.AwayFromZero),
        });
    }

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
