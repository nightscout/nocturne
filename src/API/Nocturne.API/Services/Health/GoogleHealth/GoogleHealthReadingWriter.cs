using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthReadingWriter(
    IHeartRateService heartRates,
    IStepCountService stepCounts,
    IBodyWeightService bodyWeights,
    ISleepService sleep,
    NocturneDbContext db,
    ILogger<GoogleHealthReadingWriter> logger) : IGoogleHealthReadingWriter
{
    public const string Source = DataSources.GoogleHealthConnector;
    private const string SourceApp = "Google Health";

    public async Task WriteAsync(
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<SleepSession> sleepSessions,
        int batchSize,
        CancellationToken ct)
    {
        logger.LogInformation(
            "GoogleHealthReadingWriter.WriteAsync: processing {ReadingsCount} readings and {SleepCount} sleep sessions",
            readings.Count, sleepSessions.Count);

        try
        {
            foreach (var heartRateBatch in Map(readings, "heart-rate", reading => new HeartRate
            {
                Mills = reading.Mills,
                UtcOffset = reading.UtcOffsetMinutes,
                Bpm = checked((int)reading.Value),
                Accuracy = 0,
                Device = "Google Health",
                EnteredBy = "Google Health",
                DataSource = Source,
                SyncIdentifier = GoogleHealthClient.Key(reading)
            }).Chunk(batchSize))
                await heartRates.CreateHeartRatesAsync(heartRateBatch, ct);

            foreach (var stepBatch in Map(readings, "steps", reading => new StepCount
            {
                Mills = reading.Mills,
                UtcOffset = reading.UtcOffsetMinutes,
                Metric = checked((int)reading.Value),
                Source = 0,
                Device = "Google Health",
                EnteredBy = "Google Health",
                DataSource = Source,
                SyncIdentifier = GoogleHealthClient.Key(reading)
            }).Chunk(batchSize))
                await stepCounts.CreateStepCountsAsync(stepBatch, ct);

            foreach (var weightBatch in Map(readings, "weight", reading => new BodyWeight
            {
                Mills = reading.Mills,
                UtcOffset = reading.UtcOffsetMinutes,
                WeightKg = reading.Value,
                Device = "Google Health",
                EnteredBy = "Google Health",
                DataSource = Source,
                SyncIdentifier = GoogleHealthClient.Key(reading)
            }).Chunk(batchSize))
                await bodyWeights.CreateBodyWeightsAsync(weightBatch, ct);

            foreach (var session in sleepSessions)
                await sleep.UpsertSessionAsync(session, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleHealthReadingWriter.WriteAsync failed while persisting health records");
            throw;
        }
    }

    /// <summary>
    ///     Maps one Google Health reading type to a storage record, quarantining (logging and
    ///     skipping) any single reading whose value cannot be represented — e.g. a corrupt or
    ///     out-of-range sample overflowing an int — instead of letting it fail the whole batch
    ///     and, with it, every other data type in the same tenant sync.
    /// </summary>
    private IEnumerable<TRecord> Map<TRecord>(
        IReadOnlyCollection<GoogleHealthReading> readings, string dataType, Func<GoogleHealthReading, TRecord> map)
    {
        foreach (var reading in readings.Where(reading => reading.DataType == dataType))
        {
            TRecord record;
            try
            {
                record = map(reading);
            }
            catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
            {
                logger.LogWarning(ex,
                    "Quarantining malformed Google Health {DataType} reading {SyncIdentifier}",
                    dataType, GoogleHealthClient.Key(reading));
                continue;
            }
            yield return record;
        }
    }

    public async Task ReconcileAsync(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> readingIds,
        IReadOnlyCollection<string> sleepIds,
        IReadOnlyCollection<string> activeTypes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var first = from.UtcDateTime;
        var last = to.UtcDateTime;
        var firstMills = from.ToUnixTimeMilliseconds();
        var lastMills = to.ToUnixTimeMilliseconds();
        var deletedAt = DateTime.UtcNow;
        var heartRateIds = Ids(readingIds, "heart-rate");
        var stepIds = Ids(readingIds, "steps");
        var weightIds = Ids(readingIds, "weight");

        logger.LogInformation(
            "GoogleHealthReadingWriter.ReconcileAsync: active types {ActiveTypes} from {From} to {To}. Filter IDs: {HeartRateCount} heart-rates, {StepCount} steps, {WeightCount} weights, {SleepCount} sleep sessions",
            string.Join(',', activeTypes), from, to, heartRateIds.Count, stepIds.Count, weightIds.Count, sleepIds.Count);

        try
        {

        if (activeTypes.Contains("heart-rate") && heartRateIds.Count > 0)
        {
            var existing = await db.HeartRates
                .Where(record => record.DataSource == Source && record.Timestamp >= first && record.Timestamp < last && record.DeletedAt == null)
                .Select(record => new { record.Id, record.SyncIdentifier })
                .ToListAsync(ct);
            var toDelete = existing
                .Where(record => record.SyncIdentifier != null && !heartRateIds.Contains(record.SyncIdentifier))
                .Select(record => record.Id)
                .ToList();
            foreach (var batch in toDelete.Chunk(500))
            {
                await db.HeartRates
                    .Where(record => batch.Contains(record.Id))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(record => record.DeletedAt, deletedAt)
                        .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
            }
        }

        if (activeTypes.Contains("steps") && stepIds.Count > 0)
        {
            var existing = await db.StepCounts
                .Where(record => record.DataSource == Source && record.Timestamp >= first && record.Timestamp < last && record.DeletedAt == null)
                .Select(record => new { record.Id, record.SyncIdentifier })
                .ToListAsync(ct);
            var toDelete = existing
                .Where(record => record.SyncIdentifier != null && !stepIds.Contains(record.SyncIdentifier))
                .Select(record => record.Id)
                .ToList();
            foreach (var batch in toDelete.Chunk(500))
            {
                await db.StepCounts
                    .Where(record => batch.Contains(record.Id))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(record => record.DeletedAt, deletedAt)
                        .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
            }
        }

        if (activeTypes.Contains("weight") && weightIds.Count > 0)
        {
            var existing = await db.BodyWeights
                .Where(record => record.DataSource == Source && record.Mills >= firstMills && record.Mills < lastMills && record.DeletedAt == null)
                .Select(record => new { record.Id, record.SyncIdentifier })
                .ToListAsync(ct);
            var toDelete = existing
                .Where(record => record.SyncIdentifier != null && !weightIds.Contains(record.SyncIdentifier))
                .Select(record => record.Id)
                .ToList();
            foreach (var batch in toDelete.Chunk(500))
            {
                await db.BodyWeights
                    .Where(record => batch.Contains(record.Id))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(record => record.DeletedAt, deletedAt)
                        .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
            }
        }

        if (activeTypes.Contains("sleep") && sleepIds.Count > 0)
        {
            var existing = await db.SleepSessions
                .Where(session => session.Source == SleepSource.Google.ToString() && session.SourceApp == SourceApp && session.StartTime >= first && session.StartTime < last)
                .Select(session => new { session.Id, session.OriginalId })
                .ToListAsync(ct);
            var toDelete = existing
                .Where(session => session.OriginalId != null && !sleepIds.Contains(session.OriginalId))
                .Select(session => session.Id)
                .ToList();
            foreach (var batch in toDelete.Chunk(500))
            {
                await db.SleepSessions
                    .Where(session => batch.Contains(session.Id))
                    .ExecuteDeleteAsync(ct);
            }
        }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleHealthReadingWriter.ReconcileAsync failed for active types {ActiveTypes}", string.Join(',', activeTypes));
            throw;
        }
    }

    private static IReadOnlyCollection<string> Ids(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> readingIds,
        string dataType) => readingIds.GetValueOrDefault(dataType, []);

    public async Task PurgeAsync(CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deletedAt = DateTime.UtcNow;
            await db.HeartRates.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), true), ct);
            await db.StepCounts.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), true), ct);
            await db.BodyWeights.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), true), ct);
            await db.SleepSessions.Where(session => session.Source == SleepSource.Google.ToString() && session.SourceApp == SourceApp)
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
