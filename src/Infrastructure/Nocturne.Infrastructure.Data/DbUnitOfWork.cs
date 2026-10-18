using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// One context, owned by the caller that opened it, for writes that must commit together. Every
/// other context is opened and disposed inside the one method that uses it, so two writers share a
/// transaction, an advisory lock or tracked entities only by being handed the same unit: the owner
/// opens it, passes it to each writer taking part, and commits it with <see cref="CommitAsync{T}"/>.
/// </summary>
/// <remarks>
/// A writer that accepts a unit runs on <see cref="Context"/> and never saves outside the owner's
/// <see cref="CommitAsync{T}"/>, disposes it or opens a transaction of its own; it may call
/// <c>SaveChangesAsync</c> inside the work, which the commit's transaction then holds.
/// </remarks>
public sealed class DbUnitOfWork : IAsyncDisposable
{
    private DbUnitOfWork(NocturneDbContext context) => Context = context;

    /// <summary>The context every writer in the unit runs on.</summary>
    public NocturneDbContext Context { get; }

    /// <summary>Whether <see cref="CommitAsync{T}"/>'s transaction is open.</summary>
    public bool InTransaction => Context.Database.CurrentTransaction is not null;

    /// <summary>Opens a unit on a context that reads as the scope's reader.</summary>
    public static async ValueTask<DbUnitOfWork> OpenAsync(
        INocturneDbContextFactory contexts, CancellationToken ct = default) =>
        new(await contexts.CreateAsync(ct));

    /// <summary>Opens a unit pinned to <paramref name="tenantId"/>.</summary>
    public static async ValueTask<DbUnitOfWork> OpenForTenantAsync(
        INocturneDbContextFactory contexts, Guid tenantId, CancellationToken ct = default) =>
        new(await contexts.CreateForTenantAsync(tenantId, ct));

    /// <summary>
    /// Takes ownership of <paramref name="context"/>, for an owner that opened it through an entry
    /// point the other factories do not cover. Disposing the unit disposes it.
    /// </summary>
    public static DbUnitOfWork Own(NocturneDbContext context) => new(context);

    /// <summary>
    /// The unit's commit point: runs <paramref name="work"/> in one transaction and commits it. Inside
    /// an open transaction it joins that one and the outer call commits.
    /// </summary>
    /// <remarks>
    /// Runs under <see cref="RetryingTransactionExtensions.ExecuteInTransactionAsync{T}"/>, whose
    /// remarks say how a retried attempt and a commit that reported failure are handled.
    /// </remarks>
    public Task<T> CommitAsync<T>(
        Func<CancellationToken, Task<T>> work,
        Func<T, CancellationToken, Task<bool>>? verifySucceeded = null,
        Func<object, bool>? detachBeforeEachAttempt = null,
        CancellationToken ct = default) =>
        Context.ExecuteInTransactionAsync(work, verifySucceeded, detachBeforeEachAttempt, ct);

    /// <inheritdoc cref="CommitAsync{T}"/>
    public Task CommitAsync(Func<CancellationToken, Task> work, CancellationToken ct = default) =>
        Context.ExecuteInTransactionAsync(work, ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Context.DisposeAsync();
}

/// <summary>
/// The context one call of a writer runs on: the caller's unit's when the writer was handed one,
/// else a fresh context that disposing the lease disposes.
/// </summary>
public readonly struct DbContextLease : IAsyncDisposable
{
    private readonly bool _owned;

    private DbContextLease(NocturneDbContext context, bool owned)
    {
        Context = context;
        _owned = owned;
    }

    /// <summary>The context the call runs on.</summary>
    public NocturneDbContext Context { get; }

    /// <summary>
    /// Leases <paramref name="unit"/>'s context when there is one, else a new context from
    /// <paramref name="contexts"/>.
    /// </summary>
    public static async ValueTask<DbContextLease> OpenAsync(
        DbUnitOfWork? unit, INocturneDbContextFactory? contexts, CancellationToken ct = default)
    {
        if (unit is not null)
            return new DbContextLease(unit.Context, owned: false);
        ArgumentNullException.ThrowIfNull(contexts);
        return new DbContextLease(await contexts.CreateAsync(ct), owned: true);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _owned ? Context.DisposeAsync() : ValueTask.CompletedTask;
}
