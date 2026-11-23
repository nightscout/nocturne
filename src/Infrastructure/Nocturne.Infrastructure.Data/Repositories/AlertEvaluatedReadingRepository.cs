using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary><see cref="IEvaluatedReadingStore"/> over <c>alert_evaluated_readings</c>.</summary>
public class AlertEvaluatedReadingRepository(INocturneDbContextFactory contexts) : IEvaluatedReadingStore
{
    /// <inheritdoc />
    public async Task<EvaluatedReading?> GetAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.AlertEvaluatedReadings
            .AsNoTracking()
            .Select(r => (EvaluatedReading?)new EvaluatedReading(r.ReadingTimestamp, r.ReadingMgdl, r.ReadingTrendRate))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task RecordAsync(EvaluatedReading reading, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        if (db.TenantId == Guid.Empty)
            throw new InvalidOperationException("An evaluated reading is recorded only for a tenant.");

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO alert_evaluated_readings (tenant_id, reading_timestamp, reading_mgdl, reading_trend_rate, evaluated_at)
            VALUES ({db.TenantId}, {reading.Timestamp}, {reading.Mgdl}, {reading.TrendRate}, now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                reading_timestamp = EXCLUDED.reading_timestamp,
                reading_mgdl = EXCLUDED.reading_mgdl,
                reading_trend_rate = EXCLUDED.reading_trend_rate,
                evaluated_at = EXCLUDED.evaluated_at
            """, ct);
    }
}
