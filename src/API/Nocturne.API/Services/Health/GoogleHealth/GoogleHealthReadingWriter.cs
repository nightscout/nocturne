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
        foreach (var identifier in identifiers.Distinct(StringComparer.Ordinal))
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO google_health_reconciliation_ids (run_id, tenant_id, data_type, identifier)
                VALUES ({runId}, {db.TenantId}, {dataType}, {identifier})
                ON CONFLICT (run_id, data_type, identifier) DO NOTHING
                """, ct);
    }

    public async Task CompleteReconciliationAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Database.SqlQuery<ReconciliationRun>($"""
            SELECT id, tenant_id AS "TenantId", from_time AS "FromTime", to_time AS "ToTime",
                   active_types AS "ActiveTypes"
            FROM google_health_reconciliation_runs
            WHERE id = {runId} AND tenant_id = {db.TenantId}
            """).SingleAsync(ct);
        var activeTypes = run.ActiveTypes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var deletedAt = DateTime.UtcNow;
        foreach (var type in activeTypes)
        {
            var runText = runId.ToString();
            var tenantText = db.TenantId.ToString();
            var fromText = run.FromTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var toText = run.ToTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var deletedText = deletedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var sql = type switch
            {
                "heart-rate" => $"UPDATE heart_rates SET deleted_at = '{deletedText}' WHERE data_source = '{Source}' AND deleted_at IS NULL AND timestamp >= '{fromText}' AND timestamp < '{toText}' AND sync_identifier IS NOT NULL AND NOT EXISTS (SELECT 1 FROM google_health_reconciliation_ids ids WHERE ids.run_id = '{runText}' AND ids.tenant_id = '{tenantText}' AND ids.data_type = 'heart-rate' AND ids.identifier = heart_rates.sync_identifier)",
                "steps" => $"UPDATE step_counts SET deleted_at = '{deletedText}' WHERE data_source = '{Source}' AND deleted_at IS NULL AND timestamp >= '{fromText}' AND timestamp < '{toText}' AND sync_identifier IS NOT NULL AND NOT EXISTS (SELECT 1 FROM google_health_reconciliation_ids ids WHERE ids.run_id = '{runText}' AND ids.tenant_id = '{tenantText}' AND ids.data_type = 'steps' AND ids.identifier = step_counts.sync_identifier)",
                "weight" => $"UPDATE body_weights SET deleted_at = '{deletedText}' WHERE data_source = '{Source}' AND deleted_at IS NULL AND mills >= {new DateTimeOffset(run.FromTime).ToUnixTimeMilliseconds()} AND mills < {new DateTimeOffset(run.ToTime).ToUnixTimeMilliseconds()} AND sync_identifier IS NOT NULL AND NOT EXISTS (SELECT 1 FROM google_health_reconciliation_ids ids WHERE ids.run_id = '{runText}' AND ids.tenant_id = '{tenantText}' AND ids.data_type = 'weight' AND ids.identifier = body_weights.sync_identifier)",
                "sleep" => $"DELETE FROM sleep_sessions WHERE source = 'Google' AND source_app = '{SourceApp}' AND end_time >= '{fromText}' AND end_time < '{toText}' AND original_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM google_health_reconciliation_ids ids WHERE ids.run_id = '{runText}' AND ids.tenant_id = '{tenantText}' AND ids.data_type = 'sleep' AND ids.identifier = sleep_sessions.original_id)",
                _ => throw new InvalidOperationException($"Unsupported Google Health type '{type}'")
            };
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
        await AbandonReconciliationAsync(runId, ct);
    }

    public Task AbandonReconciliationAsync(Guid runId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM google_health_reconciliation_ids WHERE run_id = {runId} AND tenant_id = {db.TenantId};
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
                .Where(session => session.Source == SleepSource.Google.ToString() && session.SourceApp == SourceApp && session.EndTime >= first && session.EndTime < last)
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
