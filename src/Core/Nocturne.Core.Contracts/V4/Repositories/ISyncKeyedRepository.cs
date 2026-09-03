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
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsRecreationBlockedAsync(
        string dataSource, string syncIdentifier, CancellationToken ct = default);
}
