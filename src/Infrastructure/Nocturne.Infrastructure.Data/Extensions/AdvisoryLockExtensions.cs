using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// A PostgreSQL transaction-scoped advisory lock on a row id, in the two-key form: the first key
/// names what is being serialised, the second is the id folded to an int. Replicas sharing the
/// database serialise on the id, and the lock goes with the commit or rollback. Other providers
/// have no other process to exclude and take nothing. Two ids whose keys collide only wait for
/// each other.
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

    private static int LockKey(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes[4..8])
            ^ BitConverter.ToInt32(bytes[8..12]) ^ BitConverter.ToInt32(bytes[12..]);
    }
}
