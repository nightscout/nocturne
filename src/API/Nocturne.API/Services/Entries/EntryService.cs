using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.API.Helpers;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// Domain service implementation for <see cref="Entry"/> operations using Store/Cache ports.
/// Delegates reads through <see cref="IEntryCache"/> with fallback to <see cref="IEntryStore"/>,
/// and writes through <see cref="IEntryDecomposer"/> to V4 tables, whose repositories raise the events.
/// </summary>
/// <seealso cref="IEntryService"/>
/// <seealso cref="IEntryStore"/>
/// <seealso cref="IEntryDecomposer"/>
/// <seealso cref="IEntryCache"/>
internal class EntryService : IEntryService
{
    private static readonly HashSet<string> ValidEntryTypes = new(
        LegacyEntryTypes.All,
        StringComparer.OrdinalIgnoreCase
    );

    private readonly IEntryStore _store;
    private readonly IEntryDecomposer _decomposer;
    private readonly IEntryCache _cache;
    private readonly ILogger<EntryService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EntryService"/>.
    /// </summary>
    /// <param name="store">The entry store for query operations.</param>
    /// <param name="decomposer">The entry decomposer for writing to V4 tables.</param>
    /// <param name="cache">The entry cache for read-through caching.</param>
    /// <param name="logger">The logger instance.</param>
    public EntryService(
        IEntryStore store,
        IEntryDecomposer decomposer,
        IEntryCache cache,
        ILogger<EntryService> logger)
    {
        _store = store;
        _decomposer = decomposer;
        _cache = cache;
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
        long mills,
        CancellationToken cancellationToken = default)
    {
        return await _store.CheckDuplicateAsync(device, type, mills, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EntryUploadResult> UpsertUploadAsync(
        IReadOnlyList<Entry> processed,
        string? uploader,
        CancellationToken cancellationToken = default)
    {
        var probes = processed
            .Select(entry => new EntryDuplicateProbe(entry.Device, entry.Type ?? "sgv", entry.Mills))
            .ToArray();

        var duplicates = await _store.CheckDuplicatesAsync(probes, cancellationToken);

        if (duplicates.Count != processed.Count)
        {
            throw new InvalidOperationException(
                $"Duplicate check returned {duplicates.Count} results for {processed.Count} entries");
        }

        var toWrite = new List<Entry>();
        var response = new List<Entry>(processed.Count);
        var glucoseMoved = new List<Entry>();
        var stored = 0;

        for (var i = 0; i < processed.Count; i++)
        {
            var entry = processed[i];
            var match = duplicates[i];

            if (match is null)
            {
                toWrite.Add(entry);
                response.Add(entry);
                glucoseMoved.Add(entry);
                continue;
            }

            stored++;

            if (match.LegacyId is null)
            {
                response.Add(match.Entry);
                continue;
            }

            var storedMgdl = EntryDecomposer.WrittenMgdl(match.Entry);
            var merged = MergeOntoStored(match.Entry, entry);
            merged.Id = match.LegacyId;
            toWrite.Add(merged);
            response.Add(merged);
            if (EntryDecomposer.WrittenMgdl(merged) != storedMgdl)
                glucoseMoved.Add(merged);
        }

        LogReuploadLoop(processed.Count, stored, uploader);

        var written = await CreateEntriesAsync(toWrite, cancellationToken: cancellationToken);

        _logger.LogDebug("Wrote {Count} entries", written.Count);

        return new EntryUploadResult(response, glucoseMoved);
    }

    /// <summary>
    /// Overlays the fields <paramref name="resent"/> carries onto <paramref name="stored"/> in place.
    /// A nullable field left null, and an <c>mgdl</c> of 0, is one the re-send did not carry.
    /// Computed and non-nullable members are always carried.
    /// </summary>
    private static Entry MergeOntoStored(Entry stored, Entry resent)
    {
        var carried = JsonSerializer.SerializeToNode(resent, CarriedFields)!.AsObject();
        if (resent.Mgdl == 0)
            carried.Remove("mgdl");

        JsonMergePatch.ApplyInPlace(stored, JsonSerializer.SerializeToElement(carried), CarrySgvAndMgdl);
        return stored;
    }

    private static readonly JsonSerializerOptions CarriedFields =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Records one line when a large upload is almost entirely already stored, which is what a
    /// client re-sending its backlog every cycle looks like from the server. Names the uploader
    /// (User-Agent) and the counts only, no entry values.
    /// </summary>
    private void LogReuploadLoop(int submitted, int duplicates, string? uploader)
    {
        if (submitted < ReuploadLoopMinimumBatch)
            return;
        if (duplicates < submitted * ReuploadLoopDuplicateRatio)
            return;

        _logger.LogInformation(
            "Entries upload of {Submitted} entries was already stored ({Duplicates} duplicates); "
                + "client {UserAgent} is re-sending stored readings",
            submitted,
            duplicates,
            LogText.Sanitize(uploader)
        );
    }

    /// <summary>Smallest upload that can be reported as a re-upload loop.</summary>
    private const int ReuploadLoopMinimumBatch = 100;

    /// <summary>Share of an upload that must already be stored to report a re-upload loop.</summary>
    private const double ReuploadLoopDuplicateRatio = 0.95;

    /// <inheritdoc />
    public async Task<Entry?> GetCurrentEntryAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrComputeCurrentAsync(
            () => _store.GetCurrentAsync(cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModifiedSincePage<Entry>> GetEntriesModifiedSinceAsync(
        long cursorMills,
        int limit,
        CancellationToken cancellationToken = default) =>
        _store.GetModifiedSinceAsync(cursorMills, limit, cancellationToken);

    /// <inheritdoc />
    public async Task<IEnumerable<Entry>> QueryEntriesAsync(
        EntryQuery query, CancellationToken cancellationToken = default) =>
        await _store.QueryAsync(query, cancellationToken);

    /// <inheritdoc />
    public Task<long> CountEntriesAsync(
        string? find = null,
        string? type = null,
        CancellationToken cancellationToken = default) =>
        _store.CountAsync(find, type, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Validates entry types, then decomposes the batch to V4 tables via
    /// <see cref="IEntryDecomposer.DecomposeBatchAsync"/>. The repository chokepoint fires the
    /// real-time <c>entries</c> broadcast per-type-batch, so this service no longer emits events directly.
    /// Entries with unrecognised types are silently filtered out.
    /// </remarks>
    public async Task<BulkWrite<Entry>> CreateEntriesAsync(
        IEnumerable<Entry> entries,
        WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default)
    {
        var validEntries = entries
            .Where(e => !string.IsNullOrEmpty(e.Type) && ValidEntryTypes.Contains(e.Type))
            .ToList();

        if (validEntries.Count == 0)
            return [];

        var result = await _decomposer.DecomposeBatchAsync(validEntries, origin, cancellationToken);

        return new BulkWrite<Entry>(validEntries, result.SkippedDeleted);
    }

    /// <inheritdoc />
    public Task<DecompositionResult> ImportEntriesAsync(
        IReadOnlyList<Entry> entries,
        CancellationToken cancellationToken = default) =>
        _decomposer.DecomposeBatchAsync(entries, WriteOrigin.Backfill, cancellationToken);

    /// <inheritdoc />
    /// <returns>The updated <see cref="Entry"/>, or <see langword="null"/> if no entry with the given <paramref name="id"/> exists.</returns>
    /// <remarks>
    /// Verifies the entry exists via the store, then writes the edit onto the stored reading
    /// <paramref name="id"/> names, as reads resolve it, through <see cref="IEntryDecomposer.DecomposeAsync"/>.
    /// The repository chokepoint fires the real-time <c>entries</c> update.
    /// </remarks>
    public async Task<Entry?> UpdateEntryAsync(
        string id,
        Entry entry,
        CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null) return null;

        // The id the request named is what the decomposer resolves the stored reading by.
        entry.Id = id;
        await _decomposer.DecomposeAsync(entry, WriteOrigin.Live, cancellationToken);

        return entry;
    }

    /// <inheritdoc />
    /// <returns>The patched <see cref="Entry"/>, or <see langword="null"/> if no entry with the given <paramref name="id"/> exists.</returns>
    /// <remarks>
    /// JSON merge-patch: overlays <paramref name="patchData"/> onto the existing entry, then writes it
    /// through <see cref="IEntryDecomposer.DecomposeAsync"/>, as <see cref="UpdateEntryAsync"/> does. AAPS's NSClientV3 PATCHes rather than PUTs entry updates.
    /// </remarks>
    public async Task<Entry?> PatchEntryAsync(
        string id,
        JsonElement patchData,
        CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null) return null;

        ApplyJsonPatch(existing, patchData);
        existing.Id = id;

        await _decomposer.DecomposeAsync(existing, WriteOrigin.Live, cancellationToken);

        return existing;
    }

    private static void ApplyJsonPatch(Entry entry, JsonElement patchData)
    {
        // The id the request named resolves the stored reading, so it must survive the merge, even when
        // the patch names _id.
        var originalId = entry.Id;

        JsonMergePatch.ApplyInPlace(entry, patchData, CarrySgvAndMgdl);

        entry.Id = originalId;
    }

    private static void CarrySgvAndMgdl(Entry patched, JsonElement patchData)
    {
        // The existing entry's sgv and mgdl are one stored value, so a patch that moves only one of them carries the other.
        var patchesSgv = patchData.TryGetProperty("sgv", out _);
        var patchesMgdl = patchData.TryGetProperty("mgdl", out _);
        if (patchesSgv && !patchesMgdl && patched.Sgv is { } sgv)
            patched.Mgdl = sgv;
        else if (patchesMgdl && !patchesSgv && patched.Mgdl != 0)
            patched.Sgv = patched.Mgdl;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolves <paramref name="id"/> to the stored record <see cref="GetEntryByIdAsync"/> returns, then
    /// deletes that record via <see cref="IEntryDecomposer.DeleteStoredAsync"/>, which routes through the
    /// repository chokepoint that fires the deletion broadcast.
    /// </remarks>
    public async Task<bool> DeleteEntryAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (await _store.GetStoredByIdAsync(id, cancellationToken) is not { } stored)
            return false;

        return await _decomposer.DeleteStoredAsync(stored, WriteOrigin.Live, cancellationToken) > 0;
    }

    /// <inheritdoc />
    /// <returns>The number of entries deleted.</returns>
    public async Task<long> DeleteEntriesAsync(
        string? find = null,
        CancellationToken cancellationToken = default)
    {
        return await _decomposer.BulkDeleteAsync(find, WriteOrigin.Live, cancellationToken);
    }
}
