using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Infrastructure.Data;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>Where <see cref="FirstTransactionFault"/> fails a write.</summary>
public enum TransactionFault
{
    SaveChanges,
    BeforeCommit,

    /// <summary>The commit lands but reports failure: a lost commit acknowledgement.</summary>
    AfterCommit,
}

/// <summary>The failure <see cref="RetryOnTransientFault"/> retries.</summary>
public sealed class TransientFault : Exception;

/// <summary>An execution strategy that retries <see cref="TransientFault"/>, as Npgsql's retries a dropped connection.</summary>
public sealed class RetryOnTransientFault(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
{
    protected override bool ShouldRetryOn(Exception exception) => exception is TransientFault;
}

/// <summary>Fails the first save or commit once, then lets every later one through.</summary>
public sealed class FirstTransactionFault(TransactionFault fault) : IDbTransactionInterceptor, ISaveChangesInterceptor
{
    private int _remaining = 1;

    /// <summary>Whether the fault has been thrown.</summary>
    public bool Fired => Volatile.Read(ref _remaining) <= 0;

    private bool Fire(TransactionFault at) => at == fault && Interlocked.Decrement(ref _remaining) == 0;

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        Fire(TransactionFault.SaveChanges) ? throw new TransientFault() : ValueTask.FromResult(result);

    public ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default) =>
        Fire(TransactionFault.BeforeCommit) ? throw new TransientFault() : ValueTask.FromResult(result);

    public Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        Fire(TransactionFault.AfterCommit) ? throw new TransientFault() : Task.CompletedTask;
}

public static class TransactionFaultOptions
{
    /// <summary>
    /// SQLite options on <paramref name="connection"/> whose execution strategy retries
    /// <paramref name="fault"/>, fired once across every context built from them.
    /// </summary>
    public static DbContextOptions<NocturneDbContext> RetryingSqlite(DbConnection connection, FirstTransactionFault fault) =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(connection, o => o.ExecutionStrategy(d => new RetryOnTransientFault(d)))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(fault)
            .Options;
}
