using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Runs work in one transaction under the context's execution strategy, so that a retried attempt
/// neither writes a row twice nor reports success for a write that did not land.
/// </summary>
/// <remarks>
/// A rolled-back attempt leaves the change tracker as it left it. An entity it added is still
/// Added, so a retry would insert it beside the retry's own copy, and an entity it saved is
/// Unchanged at its new values, so a retry setting the same values would write nothing. Before a
/// retry, and when the call fails, every entity first tracked during the call is detached and every
/// entity tracked before it is put back to the state, original values and current values it had at
/// the call. Each retry then starts from the caller's view and the work applies its edits once, and
/// a call that fails leaves the context as the caller left it. The context may be the scope's, so
/// its entities are restored rather than detached. Only scalar tracker state is restored: work must
/// not change the collection or reference navigations of an entity tracked before the call.
/// </remarks>
public static class RetryingTransactionExtensions
{
    private sealed record TrackedAtCall(
        object Entity,
        EntityState State,
        PropertyValues OriginalValues,
        PropertyValues CurrentValues,
        IReadOnlyList<string> Modified);

    /// <param name="context">The context the work writes through.</param>
    /// <param name="work">The attempt. It runs again from the start on a transient failure.</param>
    /// <param name="verifySucceeded">
    /// Judges, after a commit that reported failure, whether it landed anyway, from the result the
    /// attempt returned. When it did, that result is returned instead of running the work again.
    /// Needed only where a replayed attempt would write a second row; a unique key or an upsert
    /// already makes a replay harmless.
    /// </param>
    /// <param name="detachBeforeEachAttempt">
    /// Entities to detach before every attempt and before the verification, even when tracked
    /// before the call, so that the work reads them fresh from the store, e.g. under a lock it
    /// takes. Their state at the call is not restored.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <remarks>
    /// Joins a transaction already open on the context, and runs the work as is on a provider
    /// without transactions; the caller then owns any retry.
    /// </remarks>
    public static async Task<T> ExecuteInTransactionAsync<T>(
        this DbContext context,
        Func<CancellationToken, Task<T>> work,
        Func<T, CancellationToken, Task<bool>>? verifySucceeded = null,
        Func<object, bool>? detachBeforeEachAttempt = null,
        CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is not null || !context.Database.IsRelational())
            return await work(ct);

        bool Detaches(object entity) => detachBeforeEachAttempt?.Invoke(entity) == true;

        var atCall = context.ChangeTracker.Entries()
            .Select(e => new TrackedAtCall(
                e.Entity,
                e.State,
                e.OriginalValues.Clone(),
                e.CurrentValues.Clone(),
                e.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList()))
            .ToList();
        var trackedBefore = new HashSet<object>(atCall.Select(t => t.Entity), ReferenceEqualityComparer.Instance);

        void DetachStale()
        {
            var stale = context.ChangeTracker.Entries()
                .Where(e => !trackedBefore.Contains(e.Entity) || Detaches(e.Entity))
                .ToList();
            foreach (var entry in stale)
                entry.State = EntityState.Detached;
        }

        void Restore()
        {
            DetachStale();
            foreach (var tracked in atCall.Where(t => !Detaches(t.Entity)))
            {
                var entry = context.Entry(tracked.Entity);
                entry.State = EntityState.Unchanged;
                entry.CurrentValues.SetValues(tracked.CurrentValues);
                // Writing values flags what differs, and only a clean Unchanged entry drops those flags.
                entry.OriginalValues.SetValues(tracked.CurrentValues);
                entry.State = EntityState.Unchanged;
                entry.OriginalValues.SetValues(tracked.OriginalValues);
                if (tracked.State is EntityState.Added or EntityState.Deleted)
                    entry.State = tracked.State;
                foreach (var name in tracked.Modified)
                    entry.Property(name).IsModified = true;
            }
            context.ChangeTracker.DetectChanges();
        }

        var attempted = false;
        var committing = false;
        T result = default!;
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(
                (object?)null,
                async (_, _, token) =>
                {
                    committing = false;
                    if (attempted)
                        Restore();
                    else
                        DetachStale();
                    attempted = true;
                    await using var transaction = await context.Database.BeginTransactionAsync(token);
                    result = await work(token);
                    committing = true;
                    await transaction.CommitAsync(token);
                    return result;
                },
                verifySucceeded is null
                    ? null
                    : async (_, _, token) =>
                    {
                        if (!committing)
                            return new ExecutionResult<T>(false, default!);
                        DetachStale();
                        return new ExecutionResult<T>(await verifySucceeded(result, token), result);
                    },
                ct);
        }
        catch
        {
            Restore();
            throw;
        }
    }

    /// <summary>
    /// A <c>verifySucceeded</c> for work that inserts <paramref name="inserted"/>: one transaction
    /// commits all of them or none, so the first one's row decides. With nothing inserted there is
    /// nothing a replay could duplicate, and the work runs again.
    /// </summary>
    public static async Task<bool> AnyLandedAsync<TEntity>(
        this DbContext context, IReadOnlyList<TEntity> inserted, CancellationToken ct)
        where TEntity : class, IIdentified
    {
        if (inserted.Count == 0)
            return false;
        var id = inserted[0].Id;
        return await context.Set<TEntity>().IgnoreQueryFilters().AnyAsync(e => e.Id == id, ct);
    }

    /// <inheritdoc cref="ExecuteInTransactionAsync{T}"/>
    public static Task ExecuteInTransactionAsync(
        this DbContext context,
        Func<CancellationToken, Task> work,
        CancellationToken ct = default) =>
        context.ExecuteInTransactionAsync<object?>(
            async token =>
            {
                await work(token);
                return null;
            },
            ct: ct);
}
