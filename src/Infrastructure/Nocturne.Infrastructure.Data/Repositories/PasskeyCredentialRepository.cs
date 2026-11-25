using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IPasskeyCredentialRepository"/> over <see cref="NocturneDbContext"/>: one context per
/// call, saving each write, or the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// Every write is a tracked read and save, so a consumer's test on the InMemory provider runs it.
/// </summary>
public class PasskeyCredentialRepository : IPasskeyCredentialRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public PasskeyCredentialRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and not saved: the owner saves it, so a provisioned owner's passkey
    /// lands in one transaction with the owner and its tenant.
    /// </summary>
    public PasskeyCredentialRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewPasskeyCredential credential, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var id = Stage(db, credential);
        await SaveAsync(db, ct);
        return id;
    }

    /// <inheritdoc/>
    public virtual Task<Guid?> CreateForSetupOwnerAsync(Guid tenantId, NewPasskeyCredential credential, CancellationToken ct) =>
        SetupOwnerClaim.StoreClaimingSetupAsync(
            _contexts ?? throw new InvalidOperationException(
                "A repository over a unit of work runs inside the unit's commit and cannot open a transaction of its own."),
            tenantId,
            credential.SubjectId,
            db => Stage(db, credential),
            (db, id, token) => db.PasskeyCredentials.AnyAsync(c => c.Id == id, token),
            ct);

    private static Guid Stage(NocturneDbContext db, NewPasskeyCredential credential)
    {
        var entity = new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = credential.SubjectId,
            CredentialId = credential.CredentialId,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Transports = credential.Transports.ToList(),
            Label = credential.Label,
            AaGuid = credential.AaGuid,
            CreatedAt = credential.CreatedAt,
        };
        db.PasskeyCredentials.Add(entity);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<StoredPasskey?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.PasskeyCredentials.AsNoTracking()
            .Include(c => c.Subject)
            .FirstOrDefaultAsync(c => c.CredentialId == credentialId, ct);
        return entity?.Subject is null ? null : new StoredPasskey(ToRecord(entity), SubjectMapper.ToRecord(entity.Subject, []));
    }

    /// <inheritdoc/>
    public virtual async Task<bool> ExistsAsync(byte[] credentialId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.PasskeyCredentials.AnyAsync(c => c.CredentialId == credentialId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<byte[]>> GetCredentialIdsAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.PasskeyCredentials.AsNoTracking()
            .Where(c => c.SubjectId == subjectId)
            .Select(c => c.CredentialId)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.PasskeyCredentials.CountAsync(c => c.SubjectId == subjectId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<PasskeyCredentialRecord>> ListAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entities = await lease.Context.PasskeyCredentials.AsNoTracking()
            .Where(c => c.SubjectId == subjectId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<PasskeyHolder>> ListHoldersAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entities = await lease.Context.PasskeyCredentials.AsNoTracking()
            .Include(c => c.Subject)
            .Where(c => c.Subject != null && !c.Subject.IsSystemSubject && c.Subject.IsActive)
            .ToListAsync(ct);
        return entities
            .GroupBy(c => c.SubjectId)
            .Select(group => new PasskeyHolder(SubjectMapper.ToRecord(group.First().Subject!, []), group.Select(ToRecord).ToList()))
            .ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RecordUseAsync(Guid credentialId, uint signCount, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == credentialId, ct);
        if (entity is null)
            return false;

        entity.SignCount = signCount;
        entity.LastUsedAt = now;
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RemoveAsync(Guid credentialId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == credentialId && c.SubjectId == subjectId, ct);
        if (entity is null)
            return false;

        db.PasskeyCredentials.Remove(entity);
        await SaveAsync(db, ct);
        return true;
    }

    private static PasskeyCredentialRecord ToRecord(PasskeyCredentialEntity credential) =>
        new(
            credential.Id,
            credential.SubjectId,
            credential.CredentialId,
            credential.PublicKey,
            credential.SignCount,
            credential.Transports,
            credential.Label,
            credential.CreatedAt,
            credential.LastUsedAt,
            credential.AaGuid);
}
