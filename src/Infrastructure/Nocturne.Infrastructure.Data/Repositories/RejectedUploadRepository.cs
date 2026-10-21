using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <inheritdoc cref="IRejectedUploadStore"/>
public class RejectedUploadRepository(INocturneDbContextFactory contexts) : IRejectedUploadStore
{
    public async Task AddAsync(IReadOnlyList<RejectedUpload> uploads, CancellationToken ct = default)
    {
        if (uploads.Count == 0)
            return;

        await using var db = await contexts.CreateAsync(ct);
        db.RejectedUploads.AddRange(uploads.Select(u => new RejectedUploadEntity
        {
            RecordType = u.RecordType,
            Payload = u.Payload,
            Reason = u.Reason,
            Origin = u.Origin,
            App = u.App,
            Device = u.Device,
            ReceivedAt = u.ReceivedAt,
        }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<PaginatedResponse<RejectedUpload>> GetAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var rows = db.RejectedUploads.AsNoTracking();
        var total = await rows.CountAsync(ct);
        var page = await rows
            .OrderByDescending(e => e.ReceivedAt)
            .ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .Select(e => new RejectedUpload
            {
                Id = e.Id,
                RecordType = e.RecordType,
                Payload = e.Payload,
                Reason = e.Reason,
                Origin = e.Origin,
                App = e.App,
                Device = e.Device,
                ReceivedAt = e.ReceivedAt,
            })
            .ToListAsync(ct);

        return new PaginatedResponse<RejectedUpload>
        {
            Data = page,
            Pagination = new PaginationInfo(limit, offset, total),
        };
    }

    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.RejectedUploads.ExecuteDeleteAsync(ct);
    }
}
