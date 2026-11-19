using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// The lookups every <see cref="IProfileScoped"/> record type shares, so the five siblings a legacy
/// <see cref="Profile"/> upload decomposes into can be read and replaced through one generic surface.
/// </summary>
/// <typeparam name="TRecord">The profile-scoped record type stored by this repository.</typeparam>
public interface IProfileScopedRepository<TRecord> : ILegacyKeyedRepository<TRecord>
    where TRecord : class, IV4Record, IProfileScoped
{
    /// <summary>Retrieve all records belonging to a named profile store, newest first.</summary>
    /// <param name="profileName">The profile name to filter by.</param>
    Task<IEnumerable<TRecord>> GetByProfileNameAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent record for the given profile name that was active at-or-before the
    /// specified timestamp.
    /// </summary>
    /// <param name="profileName">The profile name to filter by.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    Task<TRecord?> GetActiveAtAsync(string profileName, DateTime timestamp, CancellationToken ct = default);

    /// <summary>Retrieve all records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records (e.g. from one profile upload).</param>
    Task<IEnumerable<TRecord>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default);

    /// <summary>Every record carrying any of <paramref name="correlationIds"/>, in one query.</summary>
    Task<IEnumerable<TRecord>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default);

    /// <summary>
    /// For each of <paramref name="profileNames"/> that has records, the one
    /// <see cref="GetByProfileNameAsync"/> returns first, in one query.
    /// </summary>
    Task<IEnumerable<TRecord>> GetNewestByProfileNamesAsync(
        IEnumerable<string> profileNames, CancellationToken ct = default);

    /// <summary>Every record filed under any of <paramref name="profileNames"/>, in one query.</summary>
    Task<IEnumerable<TRecord>> GetByProfileNamesAsync(
        IEnumerable<string> profileNames, CancellationToken ct = default);

    /// <summary>
    /// Delete all records whose legacy ObjectId starts with <paramref name="prefix"/>, replacing an
    /// entire profile upload atomically during decomposition.
    /// </summary>
    /// <param name="prefix">Legacy ObjectId prefix to match.</param>
    /// <param name="origin">Origin recorded against the delete.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdPrefixAsync(string prefix, WriteOrigin origin, CancellationToken ct = default);
}
