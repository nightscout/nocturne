using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Domain service implementation for <see cref="Entry"/> operations using Store/Cache/EventSink ports.
/// Delegates reads through <see cref="IEntryCache"/> with fallback to <see cref="IEntryStore"/>,
/// and writes through <see cref="IEntryDecomposer"/> to V4 tables with event notification via <see cref="IDataEventSink{T}"/>.
/// </summary>
/// <seealso cref="IEntryService"/>
/// <seealso cref="IEntryStore"/>
/// <seealso cref="IEntryDecomposer"/>
/// <seealso cref="IEntryCache"/>
public class EntryService : IEntryService
{
    private static readonly HashSet<string> ValidEntryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sgv", "mbg", "cal"
    };

    private readonly IEntryStore _store;
    private readonly IEntryDecomposer _decomposer;
    private readonly IEntryCache _cache;
    private readonly IDataEventSink<Entry> _events;
    private readonly ILogger<EntryService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EntryService"/>.
    /// </summary>
    /// <param name="store">The entry store for query operations.</param>
    /// <param name="decomposer">The entry decomposer for writing to V4 tables.</param>
    /// <param name="cache">The entry cache for read-through caching.</param>
    /// <param name="events">The event sink for broadcasting create/update/delete events.</param>
    /// <param name="logger">The logger instance.</param>
    public EntryService(
        IEntryStore store,
        IEntryDecomposer decomposer,
        IEntryCache cache,
        IDataEventSink<Entry> events,
        ILogger<EntryService> logger)
    {
        _store = store;
        _decomposer = decomposer;
        _cache = cache;
        _events = events;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default)
    {
        var query = new EntryQuery
        {
            Find = find,
            Count = count ?? 10,
            Skip = skip ?? 0
        };

        var cached = await _cache.GetOrComputeAsync(
            query,
            () => _store.QueryAsync(query, cancellationToken),
            cancellationToken);

        return cached ?? await _store.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesAsync(
        string? type,
        int count,
        int skip,
        CancellationToken cancellationToken)
    {
        var query = new EntryQuery
        {
            Type = type,
            Count = count,
            Skip = skip
        };

        var cached = await _cache.GetOrComputeAsync(
            query,
            () => _store.QueryAsync(query, cancellationToken),
            cancellationToken);

        return cached ?? await _store.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Entry?> GetEntryByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await _store.GetByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Entry?> CheckForDuplicateEntryAsync(
        string? device,
        string type,
        double? sgv,
        long mills,
        int windowMinutes = 5,
        CancellationToken cancellationToken = default)
    {
        return await _store.CheckDuplicateAsync(device, type, sgv, mills, windowMinutes, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Entry?> GetCurrentEntryAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrComputeCurrentAsync(
            () => _store.GetCurrentAsync(cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string find,
        int count,
        int skip,
        CancellationToken cancellationToken = default)
    {
        var query = new EntryQuery
        {
            Find = find,
            Count = count,
            Skip = skip
        };

        return await _store.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string? type,
        int count,
        int skip,
        string? findQuery,
        string? dateString,
        bool reverseResults,
        CancellationToken cancellationToken = default)
    {
        var query = new EntryQuery
        {
            Type = type,
            Find = findQuery,
            Count = count,
            Skip = skip,
            DateString = dateString,
            ReverseResults = reverseResults
        };

        return await _store.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Validates entry types, decomposes each entry directly to V4 tables via
    /// <see cref="IEntryDecomposer.DecomposeAsync"/>, then fires
    /// <see cref="IDataEventSink{T}.OnCreatedAsync(IReadOnlyList{T}, CancellationToken)"/>
    /// to trigger cache invalidation and SignalR broadcasting.
    /// Entries with unrecognised types are silently filtered out.
    /// </remarks>
    public async Task<IEnumerable<Entry>> CreateEntriesAsync(
        IEnumerable<Entry> entries,
        CancellationToken cancellationToken = default)
    {
        var validEntries = entries
            .Where(e => !string.IsNullOrEmpty(e.Type) && ValidEntryTypes.Contains(e.Type))
            .ToList();

        if (validEntries.Count == 0)
            return [];

        foreach (var entry in validEntries)
        {
            await _decomposer.DecomposeAsync(entry, cancellationToken);
        }

        await _events.OnCreatedAsync(validEntries, cancellationToken);

        return validEntries;
    }

    /// <inheritdoc />
    /// <returns>The updated <see cref="Entry"/>, or <see langword="null"/> if no entry with the given <paramref name="id"/> exists.</returns>
    /// <remarks>
    /// Verifies the entry exists via the store, then performs an idempotent upsert through
    /// <see cref="IEntryDecomposer.DecomposeAsync"/> which matches on <c>LegacyId</c>.
    /// </remarks>
    public async Task<Entry?> UpdateEntryAsync(
        string id,
        Entry entry,
        CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null) return null;

        // Ensure the entry carries the correct ID for LegacyId-based upsert
        entry.Id = id;
        await _decomposer.DecomposeAsync(entry, cancellationToken);

        await _events.OnUpdatedAsync(entry, cancellationToken);

        return entry;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deletes the entry's V4 records directly via <see cref="IEntryDecomposer.DeleteByLegacyIdAsync"/>,
    /// then broadcasts the deletion event for cache invalidation and SignalR.
    /// </remarks>
    public async Task<bool> DeleteEntryAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var entryToDelete = await _store.GetByIdAsync(id, cancellationToken);
        var deletedCount = await _decomposer.DeleteByLegacyIdAsync(id, cancellationToken);

        var deleted = deletedCount > 0;
        if (deleted)
        {
            await _events.OnDeletedAsync(entryToDelete, cancellationToken);
        }

        return deleted;
    }

    /// <inheritdoc />
    /// <returns>The number of entries deleted.</returns>
    public async Task<long> DeleteEntriesAsync(
        string? find = null,
        CancellationToken cancellationToken = default)
    {
        var deletedCount = await _decomposer.BulkDeleteAsync(find, cancellationToken);

        await _events.OnBulkDeletedAsync(deletedCount, cancellationToken);

        return deletedCount;
    }
}
