using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthReadingWriter(
    IHeartRateService heartRates,
    IStepCountService stepCounts,
    IBodyWeightService bodyWeights,
    ISleepService sleep,
    NocturneDbContext? db = null) : IGoogleHealthReadingWriter
{
    public const string Source = "google-health";

    public async Task WriteAsync(
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<SleepSession> sleepSessions,
        IReadOnlyCollection<string> activeTypes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var heartRateBatch = readings.Where(reading => reading.DataType == "heart-rate").Select(reading => new HeartRate
        {
            Mills = reading.Mills,
            UtcOffset = reading.UtcOffsetMinutes,
            Bpm = checked((int)reading.Value),
            Accuracy = 0,
            Device = "Google Health",
            EnteredBy = "Google Health",
            DataSource = Source,
            SyncIdentifier = GoogleHealthClient.Key(reading)
        }).ToArray();
        if (heartRateBatch.Length > 0) await heartRates.CreateHeartRatesAsync(heartRateBatch, ct);

        var stepBatch = readings.Where(reading => reading.DataType == "steps").Select(reading => new StepCount
        {
            Mills = reading.Mills,
            UtcOffset = reading.UtcOffsetMinutes,
            Metric = checked((int)reading.Value),
            Source = 0,
            Device = "Google Health",
            EnteredBy = "Google Health",
            DataSource = Source,
            SyncIdentifier = GoogleHealthClient.Key(reading)
        }).ToArray();
        if (stepBatch.Length > 0) await stepCounts.CreateStepCountsAsync(stepBatch, ct);

        var weightBatch = readings.Where(reading => reading.DataType == "weight").Select(reading => new BodyWeight
        {
            Mills = reading.Mills,
            UtcOffset = reading.UtcOffsetMinutes,
            WeightKg = reading.Value,
            Device = "Google Health",
            EnteredBy = "Google Health",
            DataSource = Source,
            SyncIdentifier = GoogleHealthClient.Key(reading)
        }).ToArray();
        if (weightBatch.Length > 0) await bodyWeights.CreateBodyWeightsAsync(weightBatch, ct);

        foreach (var session in sleepSessions)
            await sleep.UpsertSessionAsync(session, ct);

        if (db is not null)
            await ReconcileAsync(readings, sleepSessions, activeTypes, from, to, ct);
    }

    private async Task ReconcileAsync(
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<SleepSession> sleepSessions,
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
        var heartRateIds = Keys(readings, "heart-rate");
        var stepIds = Keys(readings, "steps");
        var weightIds = Keys(readings, "weight");
        var sleepIds = sleepSessions.Select(session => session.OriginalId!).ToArray();

        if (activeTypes.Contains("heart-rate")) await db!.HeartRates
            .Where(record => record.DataSource == Source && record.Timestamp >= first && record.Timestamp < last &&
                !heartRateIds.Contains(record.SyncIdentifier!))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.DeletedAt, deletedAt)
                .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
        if (activeTypes.Contains("steps")) await db!.StepCounts
            .Where(record => record.DataSource == Source && record.Timestamp >= first && record.Timestamp < last &&
                !stepIds.Contains(record.SyncIdentifier!))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.DeletedAt, deletedAt)
                .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
        if (activeTypes.Contains("weight")) await db!.BodyWeights
            .Where(record => record.DataSource == Source && record.Mills >= firstMills && record.Mills < lastMills &&
                !weightIds.Contains(record.SyncIdentifier!))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.DeletedAt, deletedAt)
                .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
        if (activeTypes.Contains("sleep")) await db!.SleepSessions
            .Where(session => session.Source == SleepSource.Google.ToString() &&
                session.StartTime >= first && session.StartTime < last &&
                (session.OriginalId == null || !sleepIds.Contains(session.OriginalId)))
            .ExecuteDeleteAsync(ct);
    }

    private static string[] Keys(IEnumerable<GoogleHealthReading> readings, string dataType) =>
        readings.Where(reading => reading.DataType == dataType).Select(GoogleHealthClient.Key).ToArray();

    public async Task PurgeAsync(CancellationToken ct)
    {
        if (db is null) return;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deletedAt = DateTime.UtcNow;
            await db.HeartRates.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
            await db.StepCounts.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
            await db.BodyWeights.Where(record => record.DataSource == Source)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.DeletedAt, deletedAt)
                    .SetProperty(record => EF.Property<bool>(record, "DeletedByUser"), false), ct);
            await db.SleepSessions.Where(session => session.Source == SleepSource.Google.ToString())
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
