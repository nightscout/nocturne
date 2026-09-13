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

    public async Task<Guid> BeginReconciliationAsync(
        IReadOnlyCollection<string> activeTypes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var expiresBefore = DateTime.UtcNow.AddDays(-7);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM google_health_reconciliation_runs
            WHERE tenant_id = {db.TenantId} AND created_at < {expiresBefore}
            """, ct);
        var runId = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO google_health_reconciliation_runs
                (id, tenant_id, from_time, to_time, active_types)
            VALUES ({runId}, {db.TenantId}, {from.UtcDateTime}, {to.UtcDateTime}, {string.Join(',', activeTypes)})
            """, ct);
        return runId;
    }

    public async Task StageReconciliationIdsAsync(
        Guid runId,
        string dataType,
        IReadOnlyCollection<string> identifiers,
        CancellationToken ct)
    {
        foreach (var batch in identifiers.Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                     .Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO google_health_reconciliation_ids (run_id, tenant_id, data_type, identifier)
                    SELECT run.id, run.tenant_id, {dataType}, incoming.identifier
                    FROM google_health_reconciliation_runs run
                    CROSS JOIN unnest({batch}) AS incoming(identifier)
                    WHERE run.id = {runId} AND run.tenant_id = {db.TenantId}
                    ON CONFLICT (run_id, data_type, identifier) DO NOTHING
                    """, ct);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO google_health_reconciliation_ids (run_id, tenant_id, data_type, identifier)
                SELECT run.id, run.tenant_id, {dataType}, incoming.value
                FROM google_health_reconciliation_runs run
                CROSS JOIN json_each({System.Text.Json.JsonSerializer.Serialize(batch)}) AS incoming
                WHERE run.id = {runId} AND run.tenant_id = {db.TenantId}
                ON CONFLICT (run_id, data_type, identifier) DO NOTHING
                """, ct);
        }
    }

    public async Task CompleteReconciliationAsync(Guid runId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var run = await db.Database.SqlQuery<ReconciliationRun>($"""
            SELECT id AS "Id", tenant_id AS "TenantId", from_time AS "FromTime", to_time AS "ToTime",
                   active_types AS "ActiveTypes"
            FROM google_health_reconciliation_runs
            WHERE id = {runId} AND tenant_id = {db.TenantId}
            """).SingleAsync(ct);
        var activeTypes = run.ActiveTypes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var deletedAt = DateTime.UtcNow;
        foreach (var type in activeTypes)
        {
            var identifiers = db.Database.SqlQuery<string>($"""
                SELECT identifier AS "Value" FROM google_health_reconciliation_ids
                WHERE run_id = {runId} AND tenant_id = {db.TenantId} AND data_type = {type}
                """);
            if (!await identifiers.AnyAsync(ct)) continue;
            switch (type)
            {
                case "heart-rate":
                    await db.HeartRates.Where(record => record.TenantId == db.TenantId &&
                            record.DataSource == Source && record.DeletedAt == null &&
                            record.Timestamp >= run.FromTime && record.Timestamp < run.ToTime &&
                            record.SyncIdentifier != null && !identifiers.Contains(record.SyncIdentifier))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DeletedAt, deletedAt)
                            .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
                    break;
                case "steps":
                    await db.StepCounts.Where(record => record.TenantId == db.TenantId &&
                            record.DataSource == Source && record.DeletedAt == null &&
                            record.Timestamp >= run.FromTime && record.Timestamp < run.ToTime &&
                            record.SyncIdentifier != null && !identifiers.Contains(record.SyncIdentifier))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DeletedAt, deletedAt)
                            .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
                    break;
                case "weight":
                    var firstMills = new DateTimeOffset(DateTime.SpecifyKind(run.FromTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                    var lastMills = new DateTimeOffset(DateTime.SpecifyKind(run.ToTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                    await db.BodyWeights.Where(record => record.TenantId == db.TenantId &&
                            record.DataSource == Source && record.DeletedAt == null &&
                            record.Mills >= firstMills && record.Mills < lastMills &&
                            record.SyncIdentifier != null && !identifiers.Contains(record.SyncIdentifier))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DeletedAt, deletedAt)
                            .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
                    break;
                case "sleep":
                    await db.SleepSessions.Where(session => session.TenantId == db.TenantId &&
                            session.Source == SleepSource.Google.ToString() && session.SourceApp == SourceApp &&
                            session.EndTime >= run.FromTime && session.EndTime < run.ToTime &&
                            session.OriginalId != null && !identifiers.Contains(session.OriginalId))
                        .ExecuteDeleteAsync(ct);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Google Health type '{type}'");
            }
        }
        await AbandonReconciliationAsync(runId, ct);
        await transaction.CommitAsync(ct);
        });
    }

    public Task AbandonReconciliationAsync(Guid runId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM google_health_reconciliation_runs WHERE id = {runId} AND tenant_id = {db.TenantId};
            """, ct);

    private sealed class ReconciliationRun
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public DateTime FromTime { get; init; }
        public DateTime ToTime { get; init; }
        public string ActiveTypes { get; init; } = string.Empty;
    }

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
                await WriteBatchAsync("heart-rate", heartRateBatch.Length, heartRateBatch.Max(record => record.Mills),
                    () => heartRates.CreateHeartRatesAsync(heartRateBatch, ct));

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
                await WriteBatchAsync("steps", stepBatch.Length, stepBatch.Max(record => record.Mills),
                    () => stepCounts.CreateStepCountsAsync(stepBatch, ct));

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
                await WriteBatchAsync("weight", weightBatch.Length, weightBatch.Max(record => record.Mills),
                    () => bodyWeights.CreateBodyWeightsAsync(weightBatch, ct));

            foreach (var session in sleepSessions)
                await WriteBatchAsync("sleep", 1,
                    new DateTimeOffset(DateTime.SpecifyKind(session.EndTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    () => sleep.UpsertSessionAsync(session, ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleHealthReadingWriter.WriteAsync failed while persisting health records");
            throw;
        }
    }

    private async Task WriteBatchAsync(string dataType, int count, long latestMills, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch
        {
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
