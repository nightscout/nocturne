using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// A PostgreSQL transaction-scoped advisory lock on a row id, in the two-key form: the first key
/// names what is being serialised, the second is the id folded to an int. Transactions on any
/// connection serialise on the id, and the lock goes with the commit or rollback. Other providers
/// take nothing, so tests on the in-memory and SQLite providers do not exercise the lock. Two ids
/// whose keys collide only wait for each other, or turn each other's try away.
/// </summary>
public static class AdvisoryLockExtensions
{
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    public static async Task LockForTransactionAsync(
        this DbContext context,
        int lockClass,
        Guid id,
        CancellationToken ct = default)
    {
        if (!context.Database.IsNpgsql())
            return;
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An advisory lock is taken inside a transaction");

        await context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({lockClass}, {LockKey(id)})", ct);
    }

    /// <summary>
    /// The lock on a text key, hashed to the second int key, for a row that may not exist yet.
    /// Two keys whose hashes collide only wait for each other.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    public static async Task LockForTransactionAsync(
        this DbContext context,
        int lockClass,
        string key,
        CancellationToken ct = default)
    {
        if (!context.Database.IsNpgsql())
            return;
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An advisory lock is taken inside a transaction");

        await context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({lockClass}, hashtext({key}))", ct);
    }

    /// <summary>
    /// Takes the lock only when no other transaction holds it. Other providers always get it.
    /// </summary>
    /// <returns>Whether this transaction now holds the lock.</returns>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    public static async Task<bool> TryLockForTransactionAsync(
        this DbContext context,
        int lockClass,
        Guid id,
        CancellationToken ct = default)
    {
        if (!context.Database.IsNpgsql())
            return true;
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An advisory lock is taken inside a transaction");

        return await context.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({lockClass}, {LockKey(id)}) AS \"Value\"")
            .SingleAsync(ct);
    }

    private static int LockKey(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes[4..8])
            ^ BitConverter.ToInt32(bytes[8..12]) ^ BitConverter.ToInt32(bytes[12..]);
    }
}
