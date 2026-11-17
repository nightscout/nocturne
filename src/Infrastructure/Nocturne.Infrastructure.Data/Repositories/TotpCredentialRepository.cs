using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ITotpCredentialRepository"/> over <see cref="NocturneDbContext"/>, one context per
/// call. The secret column is decrypted by the value converter when a row is materialized, so the
/// reads that do not need it project around it.
/// </summary>
public class TotpCredentialRepository : ITotpCredentialRepository
{
    private readonly INocturneDbContextFactory _contexts;

    public TotpCredentialRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewTotpCredential credential, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = new TotpCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = credential.SubjectId,
            SecretKey = credential.SecretKey,
            Label = credential.Label,
            CreatedAt = credential.CreatedAt,
            LastUsedStep = credential.LastUsedStep,
            LastUsedAt = credential.LastUsedAt,
        };
        db.TotpCredentials.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TotpCredentialSummary>> ListAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await db.TotpCredentials.AsNoTracking()
            .Where(c => c.SubjectId == subjectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new TotpCredentialSummary(c.Id, c.Label, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TotpCredentialSecret>> ListSecretsAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var credentials = await db.TotpCredentials.AsNoTracking()
            .Where(c => c.SubjectId == subjectId)
            .ToListAsync(ct);
        return credentials.Select(c => new TotpCredentialSecret(c.Id, c.SecretKey, c.LastUsedStep)).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryConsumeStepAsync(Guid credentialId, long step, DateTime now, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var consumed = await db.TotpCredentials
            .Where(c => c.Id == credentialId && (c.LastUsedStep == null || c.LastUsedStep < step))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.LastUsedStep, step)
                .SetProperty(c => c.LastUsedAt, now), ct);
        return consumed == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RemoveAsync(Guid credentialId, Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        // Key-only projection, then delete a stub: loading the entity would decrypt secret_key and
        // throw, leaving an undecryptable credential undeletable, the case where removal matters
        // most, since re-enrolling is the recovery. ExecuteDeleteAsync would also skip the
        // materialization but is unsupported by the InMemory provider the consumers' tests use.
        var existingId = await db.TotpCredentials
            .Where(c => c.Id == credentialId && c.SubjectId == subjectId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (existingId is null)
            return false;

        db.TotpCredentials.Remove(new TotpCredentialEntity { Id = existingId.Value });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await db.TotpCredentials.CountAsync(c => c.SubjectId == subjectId, ct);
    }
}
