using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.Health;

public sealed class HeartRateRepository
    : ObservedHealthSeriesRepository<HeartRate, HeartRateEntity>, IHeartRateRepository
{
    public HeartRateRepository(INocturneDbContextFactory contexts, TimeProvider time)
        : base(contexts, time)
    {
    }

    internal HeartRateRepository(DbUnitOfWork unit, TimeProvider time)
        : base(unit, time)
    {
    }

    protected override DbSet<HeartRateEntity> Set(NocturneDbContext db) => db.HeartRates;

    protected override HeartRate ToDomain(HeartRateEntity entity) => HeartRateMapper.ToDomainModel(entity);

    protected override HeartRateEntity ToEntity(HeartRate model) => HeartRateMapper.ToEntity(model);

    protected override void Apply(HeartRateEntity entity, HeartRate model) => HeartRateMapper.UpdateEntity(entity, model);

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeartRate>> GetMinuteAveragesAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var rangeStart = from;
        var rangeEnd = to;
        var buckets = await (
            from row in db.HeartRates.AsNoTracking()
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
            }).ToListAsync(ct);

        return buckets.Select(bucket => new HeartRate
        {
            Timestamp = new DateTime(bucket.Year, bucket.Month, bucket.Day,
                bucket.Hour, bucket.Minute,
                0,
                DateTimeKind.Utc),
            Bpm = (int)Math.Round(bucket.AverageBpm, MidpointRounding.AwayFromZero),
        }).ToList();
    }
}
