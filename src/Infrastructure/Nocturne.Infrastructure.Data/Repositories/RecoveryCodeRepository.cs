using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IRecoveryCodeRepository"/> over <see cref="NocturneDbContext"/>, one context per call.
/// The consume is an <c>ExecuteUpdate</c> whose predicate is the code's liveness, so the row's
/// lock, not the change tracker, decides which of two presentations of a code spends it.
/// </summary>
public class RecoveryCodeRepository : IRecoveryCodeRepository
{
    private readonly INocturneDbContextFactory _contexts;

    public RecoveryCodeRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc/>
    public virtual async Task ReplaceAsync(Guid subjectId, IReadOnlyList<string> codeHashes, DateTime now, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var existing = await db.RecoveryCodes.Where(r => r.SubjectId == subjectId).ToListAsync(ct);
        db.RecoveryCodes.RemoveRange(existing);
        foreach (var hash in codeHashes)
        {
            db.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                CodeHash = hash,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<RecoveryCodeHash>> GetLiveAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await Live(db, subjectId)
            .OrderBy(r => r.Id)
            .Select(r => new RecoveryCodeHash(r.Id, r.CodeHash))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryConsumeAsync(Guid codeId, DateTime now, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var consumed = await db.RecoveryCodes
            .Where(r => r.Id == codeId && r.UsedAt == null && r.InvalidatedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, now), ct);
        return consumed == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<int> InvalidateLiveAsync(Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var live = await Live(db, subjectId).ToListAsync(ct);
        foreach (var code in live)
            code.InvalidatedAt = now;

        await db.SaveChangesAsync(ct);
        return live.Count;
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountLiveAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await Live(db, subjectId).CountAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> HasCodesAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await db.RecoveryCodes.AnyAsync(r => r.SubjectId == subjectId && r.InvalidatedAt == null, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyInvalidatedAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await db.RecoveryCodes.AnyAsync(r => r.SubjectId == subjectId && r.InvalidatedAt != null, ct);
    }

    private static IQueryable<RecoveryCodeEntity> Live(NocturneDbContext db, Guid subjectId) =>
        db.RecoveryCodes.Where(r => r.SubjectId == subjectId && r.UsedAt == null && r.InvalidatedAt == null);
}
