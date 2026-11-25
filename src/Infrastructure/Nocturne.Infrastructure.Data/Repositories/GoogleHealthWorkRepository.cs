using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IGoogleHealthWorkStore"/> over <c>google_health_work</c>. Each write is one upsert that
/// sets only its own part's columns, so a progress writer and a disconnect writer never overwrite
/// each other, and concurrent first writes land in one row.
/// </summary>
public class GoogleHealthWorkRepository(INocturneDbContextFactory contexts) : IGoogleHealthWorkStore
{
    private static readonly IReadOnlyDictionary<GoogleHealthSyncPhase, string> PhaseNames =
        Enum.GetValues<GoogleHealthSyncPhase>().ToDictionary(phase => phase, phase => JsonSerializer.Serialize(phase).Trim('"'));

    private static readonly IReadOnlyDictionary<string, GoogleHealthSyncPhase> Phases =
        PhaseNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>A phase that is not one of <see cref="GoogleHealthSyncPhase"/>'s names reads as no progress.</remarks>
    public async Task<GoogleHealthWork> GetAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.GoogleHealthWork.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null)
            return GoogleHealthWork.None;

        return new GoogleHealthWork(
            row.ProgressPhase is { } name && Phases.TryGetValue(name, out var phase)
                ? new GoogleHealthSyncProgress(phase, row.ProgressDataType, row.ProgressCompletedDataTypes,
                    row.ProgressTotalDataTypes, row.ProgressPagesRead, row.ProgressWorkerOwned)
                : null,
            row is { DisconnectRequestId: { } requestId, DisconnectSubjectId: { } subjectId }
                ? new GoogleHealthDisconnectRequest(requestId, subjectId)
                : null);
    }

    /// <inheritdoc />
    public async Task SetProgressAsync(GoogleHealthSyncProgress? progress, CancellationToken ct)
    {
        await using var db = await TenantContextAsync(ct);
        var phase = progress is null ? null : PhaseNames[progress.Phase];
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO google_health_work (tenant_id, progress_phase, progress_data_type, progress_completed_data_types,
                                            progress_total_data_types, progress_pages_read, progress_worker_owned)
            VALUES ({db.TenantId}, {phase}, {progress?.DataType}, {progress?.CompletedDataTypes ?? 0},
                    {progress?.TotalDataTypes ?? 0}, {progress?.PagesRead ?? 0}, {progress?.WorkerOwned ?? false})
            ON CONFLICT (tenant_id) DO UPDATE SET
                progress_phase = EXCLUDED.progress_phase,
                progress_data_type = EXCLUDED.progress_data_type,
                progress_completed_data_types = EXCLUDED.progress_completed_data_types,
                progress_total_data_types = EXCLUDED.progress_total_data_types,
                progress_pages_read = EXCLUDED.progress_pages_read,
                progress_worker_owned = EXCLUDED.progress_worker_owned
            """, ct);
    }

    /// <inheritdoc />
    public async Task SetDisconnectAsync(GoogleHealthDisconnectRequest? request, CancellationToken ct)
    {
        await using var db = await TenantContextAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO google_health_work (tenant_id, progress_completed_data_types, progress_total_data_types,
                                            progress_pages_read, progress_worker_owned,
                                            disconnect_request_id, disconnect_subject_id)
            VALUES ({db.TenantId}, 0, 0, 0, false, {request?.RequestId}, {request?.SubjectId})
            ON CONFLICT (tenant_id) DO UPDATE SET
                disconnect_request_id = EXCLUDED.disconnect_request_id,
                disconnect_subject_id = EXCLUDED.disconnect_subject_id
            """, ct);
    }

    private async Task<NocturneDbContext> TenantContextAsync(CancellationToken ct)
    {
        var db = await contexts.CreateAsync(ct);
        if (db.TenantId != Guid.Empty)
            return db;

        await db.DisposeAsync();
        throw new InvalidOperationException("Google Health work is kept only for a tenant.");
    }
}
