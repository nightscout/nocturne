using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <inheritdoc cref="IRejectedUploadStore"/>
/// <remarks>
/// A tenant keeps its <see cref="RetainedPerTenant"/> most recently seen rejected uploads; a new
/// one beyond that drops the least recently seen. A misbehaving uploader resending the same records
/// only counts occurrences, and one sending ever-new unstorable records cannot grow the table
/// without bound.
/// </remarks>
public class RejectedUploadRepository(INocturneDbContextFactory contexts, IAuditContext auditContext)
    : IRejectedUploadStore
{
    public const int RetainedPerTenant = 500;

    public async Task<bool> UpsertAsync(RejectedUpload upload, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        // Materialised as a list: a single-row operator would wrap the INSERT in a SELECT.
        var inserted = (await db.Database.SqlQuery<bool>($"""
            INSERT INTO rejected_uploads AS r
                (id, tenant_id, record_type, identity, payload, payload_bytes, reason, origin, app, device,
                 occurrences, first_seen_at, last_seen_at)
            VALUES
                ({Guid.CreateVersion7()}, {db.TenantId}, {upload.RecordType}, {upload.Identity},
                 {upload.Payload}::jsonb, {upload.PayloadBytes}, {upload.Reason}, {Fit(upload.Origin)},
                 {Fit(upload.App)}, {Fit(upload.Device)}, 1, {upload.LastSeenAt}, {upload.LastSeenAt})
            ON CONFLICT (tenant_id, record_type, identity) DO UPDATE SET
                payload = EXCLUDED.payload,
                payload_bytes = EXCLUDED.payload_bytes,
                reason = EXCLUDED.reason,
                origin = EXCLUDED.origin,
                app = EXCLUDED.app,
                device = EXCLUDED.device,
                occurrences = r.occurrences + 1,
                last_seen_at = EXCLUDED.last_seen_at
            RETURNING (xmax = 0) AS "Value"
            """).ToListAsync(ct)).Single();

        if (inserted)
        {
            var retained = db.RejectedUploads
                .OrderByDescending(e => e.LastSeenAt)
                .ThenByDescending(e => e.Id)
                .Take(RetainedPerTenant)
                .Select(e => e.Id);
            await db.RejectedUploads.Where(e => !retained.Contains(e.Id)).ExecuteDeleteAsync(ct);
        }

        return inserted;
    }

    public async Task<PaginatedResponse<RejectedUpload>> GetAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var rows = db.RejectedUploads.AsNoTracking();
        var total = await rows.CountAsync(ct);
        var page = await rows
            .OrderByDescending(e => e.LastSeenAt)
            .ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .Select(e => new RejectedUpload
            {
                Id = e.Id,
                RecordType = e.RecordType,
                Identity = e.Identity,
                Payload = e.Payload,
                PayloadBytes = e.PayloadBytes,
                Reason = e.Reason,
                Origin = e.Origin,
                App = e.App,
                Device = e.Device,
                Occurrences = e.Occurrences,
                FirstSeenAt = e.FirstSeenAt,
                LastSeenAt = e.LastSeenAt,
            })
            .ToListAsync(ct);

        return new PaginatedResponse<RejectedUpload>
        {
            Data = page,
            Pagination = new PaginationInfo(limit, offset, total),
        };
    }

    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return 0;

        await using var db = await contexts.CreateAsync(ct);
        return await db.AuditedExecuteDeleteAsync(db.RejectedUploads.Where(e => ids.Contains(e.Id)), auditContext, ct);
    }

    /// <summary>The cut <see cref="Entities.FitToColumnAttribute"/> makes, which raw SQL bypasses.</summary>
    private static string? Fit(string? value) =>
        value is { Length: > 256 } ? value[..(char.IsHighSurrogate(value[255]) ? 255 : 256)] : value;
}
