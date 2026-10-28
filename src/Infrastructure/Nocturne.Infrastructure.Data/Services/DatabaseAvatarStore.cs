using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Storage;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Stores and retrieves subject avatar images in PostgreSQL (bytea column).
/// </summary>
public class DatabaseAvatarStore(IDbContextFactory<NocturneDbContext> contextFactory, TimeProvider time) : IAvatarStore
{
    /// <inheritdoc />
    public async Task<string> SaveAsync(Guid subjectId, Stream image, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await image.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var existing = await db.SubjectAvatars.FirstOrDefaultAsync(a => a.SubjectId == subjectId, ct);
        var now = time.GetUtcNow().UtcDateTime;

        if (existing is not null)
        {
            existing.Data = bytes;
            existing.ContentType = contentType;
            existing.FileSize = bytes.Length;
            existing.CreatedAt = now;
        }
        else
        {
            db.SubjectAvatars.Add(new SubjectAvatarEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                Data = bytes,
                ContentType = contentType,
                FileSize = bytes.Length,
                CreatedAt = now,
            });
        }

        // Update the subject's avatar URL to the serving endpoint
        var subject = await db.Subjects.FirstAsync(s => s.Id == subjectId, ct);
        subject.AvatarUrl = $"/api/v4/me/avatar?id={subjectId}";

        await db.SaveChangesAsync(ct);
        return subject.AvatarUrl;
    }

    /// <inheritdoc />
    public async Task<AvatarData?> GetAsync(Guid subjectId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var entity = await db.SubjectAvatars
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.SubjectId == subjectId, ct);

        if (entity is null) return null;

        return new AvatarData(new MemoryStream(entity.Data), entity.ContentType, entity.FileSize);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid subjectId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var avatar = await db.SubjectAvatars.FirstOrDefaultAsync(a => a.SubjectId == subjectId, ct);
        if (avatar is not null)
        {
            db.SubjectAvatars.Remove(avatar);
        }

        var subject = await db.Subjects.FirstOrDefaultAsync(s => s.Id == subjectId, ct);
        if (subject is not null)
        {
            subject.AvatarUrl = null;
        }

        await db.SaveChangesAsync(ct);
    }
}
