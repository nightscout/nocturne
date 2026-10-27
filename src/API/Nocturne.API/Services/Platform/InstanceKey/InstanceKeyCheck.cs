using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Platform.InstanceKey;

/// <summary>
/// The stored check that tells whether the instance key is the one the stored secrets were
/// encrypted with (<c>instance_key_check</c>).
/// </summary>
public class InstanceKeyCheck(INocturneDbContextFactory contexts, ISecretEncryptionService encryption)
{
    internal const string KnownValue = "nocturne-instance-key-check";

    /// <summary>How recently the current key must have scanned for an older key to defer to it.</summary>
    internal static readonly TimeSpan LivenessWindow = 2 * InstanceKeyChangeService.RescanInterval;

    /// <summary>
    /// Whether the current key is the one the stored secrets were last checked under. A check the
    /// current key cannot read is rewritten under it with a new generation, marked outstanding, and
    /// the old check kept as the previous one: the generation names the key that last read it, so a
    /// later change to a third key gets a generation, and notices, of its own. A key that reads only
    /// the previous check is <see cref="InstanceKeyState.Superseded"/> and changes nothing while the
    /// current key's outstanding change was scanned within <see cref="LivenessWindow"/>, so two live
    /// replicas on two keys do not take the check from each other every pass. Past that, nobody runs
    /// the current key, and the previous key returning is a change like any other. A missing check is
    /// written under the current key, so the first start, and an upgrade to the release that adds the
    /// check, find no change. No key configured reads as current.
    /// </summary>
    public async Task<InstanceKeyFinding> FindChangeAsync(CancellationToken cancellationToken)
    {
        if (!encryption.IsConfigured)
            return new InstanceKeyFinding(InstanceKeyState.Current, Guid.Empty);

        await using var db = await contexts.CreatePlatformAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO instance_key_check (id, generation, ciphertext, outstanding)
            VALUES ({InstanceKeyCheckEntity.SingletonId}, {Guid.CreateVersion7()}, {encryption.Encrypt(KnownValue)}, false)
            ON CONFLICT (id) DO NOTHING
            """,
            cancellationToken);

        var check = await ReadAsync(db, cancellationToken);
        if (!Reads(check.Ciphertext))
        {
            if (Reads(check.PreviousCiphertext) && await ScannedRecentlyAsync(db, check.Generation, cancellationToken))
                return new InstanceKeyFinding(InstanceKeyState.Superseded, check.Generation);

            var ciphertext = encryption.Encrypt(KnownValue);
            await db.InstanceKeyChecks
                .Where(c => c.Id == InstanceKeyCheckEntity.SingletonId && c.Generation == check.Generation)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Generation, Guid.CreateVersion7())
                    .SetProperty(c => c.PreviousCiphertext, check.Ciphertext)
                    .SetProperty(c => c.Ciphertext, ciphertext)
                    .SetProperty(c => c.Outstanding, true)
                    .SetProperty(c => c.CheckedAt, _ => DateTime.UtcNow), cancellationToken);
            check = await ReadAsync(db, cancellationToken);
            if (!Reads(check.Ciphertext))
                return new InstanceKeyFinding(InstanceKeyState.Superseded, check.Generation);
        }

        if (!check.Outstanding)
            return new InstanceKeyFinding(InstanceKeyState.Current, check.Generation);

        await db.InstanceKeyChecks
            .Where(c => c.Id == InstanceKeyCheckEntity.SingletonId && c.Generation == check.Generation)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CheckedAt, _ => DateTime.UtcNow), cancellationToken);
        return new InstanceKeyFinding(InstanceKeyState.Changed, check.Generation);
    }

    /// <remarks>
    /// <see cref="DateTime.UtcNow"/> in the query is the database clock (<c>now()</c>), so replicas
    /// whose clocks disagree still agree on what is recent.
    /// </remarks>
    private static Task<bool> ScannedRecentlyAsync(
        Infrastructure.Data.NocturneDbContext db, Guid generation, CancellationToken cancellationToken) =>
        db.InstanceKeyChecks.AnyAsync(
            c => c.Id == InstanceKeyCheckEntity.SingletonId
                && c.Generation == generation
                && c.Outstanding
                && c.CheckedAt > DateTime.UtcNow.AddSeconds(-LivenessWindow.TotalSeconds),
            cancellationToken);

    /// <summary>
    /// Ends the outstanding change, if it is still the generation the caller found. A start under a
    /// newer key leaves its own change alone.
    /// </summary>
    public async Task ClearAsync(Guid generation, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreatePlatformAsync(cancellationToken);
        await db.InstanceKeyChecks
            .Where(c => c.Id == InstanceKeyCheckEntity.SingletonId && c.Generation == generation)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Outstanding, false), cancellationToken);
    }

    private bool Reads(string? ciphertext) =>
        !string.IsNullOrEmpty(ciphertext) && encryption.CanDecrypt(ciphertext) && encryption.Decrypt(ciphertext) == KnownValue;

    private static Task<InstanceKeyCheckEntity> ReadAsync(
        Infrastructure.Data.NocturneDbContext db, CancellationToken cancellationToken) =>
        db.InstanceKeyChecks.AsNoTracking()
            .SingleAsync(c => c.Id == InstanceKeyCheckEntity.SingletonId, cancellationToken);
}

public enum InstanceKeyState
{
    /// <summary>The key reads the check and no change is outstanding.</summary>
    Current,

    /// <summary>A change to this key is outstanding.</summary>
    Changed,

    /// <summary>The key was replaced by a newer one that another replica runs on.</summary>
    Superseded,
}

/// <param name="Generation">The check's generation as this key found it.</param>
public readonly record struct InstanceKeyFinding(InstanceKeyState State, Guid Generation)
{
    /// <summary>The generation of the outstanding change this key acts on, or null when there is none.</summary>
    public Guid? Change => State == InstanceKeyState.Changed ? Generation : null;
}
