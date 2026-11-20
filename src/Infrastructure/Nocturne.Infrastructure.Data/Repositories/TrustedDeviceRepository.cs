using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ITrustedDeviceRepository"/> over <see cref="NocturneDbContext"/>, one context per
/// call. Writes are tracked saves: the InMemory provider its consumers' tests run on has no
/// <c>ExecuteUpdate</c> or <c>ExecuteDelete</c>, and a subject holds a handful of rows at most.
/// </summary>
public class TrustedDeviceRepository : ITrustedDeviceRepository
{
    private readonly INocturneDbContextFactory _contexts;

    public TrustedDeviceRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewTrustedDevice device, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = new TrustedDeviceEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = device.SubjectId,
            TokenHash = device.TokenHash,
            DeviceDescription = device.DeviceDescription,
            IpAddress = device.IpAddress,
            CreatedAt = device.CreatedAt,
            ExpiresAt = device.ExpiresAt,
        };
        db.TrustedDevices.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<TrustedDeviceRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.TrustedDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TokenHash == tokenHash, ct);
        return entity is null ? null : ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task MarkUsedAsync(Guid deviceId, DateTime now, string? ipAddress, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.TrustedDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (entity is null)
            return;

        entity.LastUsedAt = now;
        if (!string.IsNullOrEmpty(ipAddress))
            entity.IpAddress = ipAddress;
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TrustedDeviceRecord>> ListAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entities = await db.TrustedDevices.AsNoTracking()
            .Where(d => d.SubjectId == subjectId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RemoveAsync(Guid deviceId, Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.TrustedDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.SubjectId == subjectId, ct);
        if (entity is null)
            return false;

        db.TrustedDevices.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<int> RemoveAllAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entities = await db.TrustedDevices.Where(d => d.SubjectId == subjectId).ToListAsync(ct);
        if (entities.Count == 0)
            return 0;

        db.TrustedDevices.RemoveRange(entities);
        await db.SaveChangesAsync(ct);
        return entities.Count;
    }

    private static TrustedDeviceRecord ToRecord(TrustedDeviceEntity entity) => new(
        entity.Id,
        entity.SubjectId,
        entity.TokenHash,
        entity.DeviceDescription,
        entity.IpAddress,
        entity.CreatedAt,
        entity.LastUsedAt,
        entity.ExpiresAt);
}
