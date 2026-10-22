using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// The (DataSource, SyncIdentifier) surface shared by the record types whose creates upsert on the
/// sync key.
/// </summary>
/// <typeparam name="T">The V4 record type stored by this repository.</typeparam>
public interface ISyncKeyedRepository<T> where T : class, IV4Record
{
    /// <summary>
    /// Whether a create on <paramref name="dataSource"/>/<paramref name="syncIdentifier"/> would be
    /// refused with <see cref="RecreationBlockedException"/> — the key is held by a row the user
    /// deleted and by no live row.
    /// </summary>
    /// <remarks>
    /// For callers that write two records under one key across separate contexts, so the refusal is
    /// known before the first write rather than after a committed half.
    /// </remarks>
    /// <param name="dataSource">The connector or uploader that minted the key.</param>
    /// <param name="syncIdentifier">The key's per-source identifier.</param>
    Task<bool> IsRecreationBlockedAsync(
        string dataSource, string syncIdentifier, CancellationToken ct = default);

    /// <summary>
    /// <see cref="IV4Repository{T}.CreateAsync"/>, also reporting whether the write inserted a row or
    /// updated in place the live row holding the same (DataSource, SyncIdentifier).
    /// </summary>
    /// <exception cref="RecreationBlockedException">The key is held by a row the user deleted.</exception>
    Task<SyncUpsert<T>> UpsertAsync(T model, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// The stored timestamps of the live rows a create of <paramref name="records"/> would update in
    /// place, matched on (DataSource, SyncIdentifier). The create moves each such row to its
    /// record's timestamp.
    /// </summary>
    /// <param name="records">The records about to be created.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DateTime>> GetUpsertTargetTimestampsAsync(
        IReadOnlyCollection<T> records, CancellationToken ct = default);
}

/// <summary>
/// The outcome of <see cref="ISyncKeyedRepository{T}.UpsertAsync"/>: the persisted record and whether
/// it was inserted rather than updated in place.
/// </summary>
/// <typeparam name="TRecord">The V4 record type.</typeparam>
public sealed record SyncUpsert<TRecord>(TRecord Record, bool Created);
