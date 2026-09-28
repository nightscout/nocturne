using Nocturne.API.Services.Audit;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// The create-or-update-by-<c>LegacyId</c> body and the bulk-insert tail shared by the
/// repository-backed decomposers.
/// </summary>
public abstract class DecomposerBase
{
    protected ILogger Logger { get; }

    protected DecomposerBase(ILogger logger) => Logger = logger;

    /// <summary>
    /// Create-or-update under <paramref name="legacyId"/>. <paramref name="beforeWrite"/> runs once the
    /// stored record is known and before the write, so device-attributed types can settle their
    /// attribution against it; see <see cref="StampAttributionAsync"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="preserveStoredCorrelationId"/> treats the stored correlation id as belonging to
    /// the row rather than to the decomposition that last touched it. A decomposer mints a fresh id per
    /// call, so re-upserting an otherwise unchanged row modifies <see cref="IV4Record.CorrelationId"/>
    /// and makes EF emit an UPDATE — one carrying no material change, so it neither audits nor
    /// broadcasts, but still writes a row version. The column is indexed, so that update cannot be HOT
    /// and appends to every index on the table; Npgsql writes a <see cref="Guid"/> big-endian, so a
    /// UUID v7 id is byte-monotonic in the btree and those appends rarely refill the pages the dead
    /// entries freed — only vacuum returns an entirely empty one, and not before two cycles — which is
    /// why density collapses rather than settling. An empty stored id is ignored, so it still
    /// self-heals on the next write rather than being frozen and stamped across the group; a non-empty
    /// one is preserved regardless of origin, since nothing distinguishes a client-supplied id from a
    /// decomposer-minted one.
    /// <para>
    /// Only an anchor record may set this, and only where the caller then stamps the rest of the group
    /// from what it reads back. Preserving per row instead lets a group fork permanently: siblings are
    /// written in separate transactions, so one lost to a cancelled sync is recreated under a fresh id
    /// while the survivors keep the old one, and a reader that loads siblings by correlation id then
    /// silently returns nothing. Rewriting the id everywhere on every sync is what currently repairs
    /// that, so removing the rewrite without converging the group would make the damage permanent.
    /// A group whose members are not all upserted under a shared legacy id has no anchor to converge
    /// on and must not set this at all: <see cref="DeviceStatusDecomposer"/> creates its extras row
    /// keyed by the freshly minted id and carrying no legacy id, so preserving on one of its snapshot
    /// siblings orphans that row outright.
    /// </para>
    /// </remarks>
    /// <param name="findStored">
    /// Resolves the stored record in place of the lookup by <paramref name="legacyId"/>, for a caller
    /// that already knows it, including a stored record with no legacy id at all.
    /// </param>
    /// <returns>
    /// The persisted record and whether it was inserted rather than updated, or <see langword="null"/>
    /// when the write was refused because the record's identity is already held
    /// (<see cref="RecreationBlockedException"/>) — the outcome the batch path reaches by dropping
    /// the record from its insert set. It is counted in <see cref="DecompositionResult.SkippedDeleted"/>.
    /// </returns>
    protected async Task<(TRecord Record, bool Created)?> UpsertByLegacyIdAsync<TRecord>(
        ILegacyKeyedRepository<TRecord> repository,
        string? legacyId,
        TRecord model,
        DecompositionResult result,
        WriteOrigin origin,
        CancellationToken ct,
        Func<TRecord?, Task>? beforeWrite = null,
        bool preserveStoredCorrelationId = false,
        Func<Task<TRecord?>>? findStored = null)
        where TRecord : class, IV4Record
    {
        var existing = findStored is not null
            ? await findStored()
            : legacyId is null ? null : await repository.GetByLegacyIdAsync(legacyId, ct);

        if (beforeWrite is not null)
            await beforeWrite(existing);

        var recordType = typeof(TRecord).Name;

        if (existing is null)
        {
            TRecord created;
            try
            {
                created = await repository.CreateAsync(model, origin, ct);
            }
            catch (RecreationBlockedException blocked)
            {
                // No live row carries the legacy id, so what holds it is the user's deletion.
                result.SkippedDeleted++;
                if (blocked.HeldBy is { } heldBy)
                    result.RefusedRecords.Add(new RefusedRecord(typeof(TRecord), heldBy));
                Logger.LogDebug("Skipped a {RecordType}: its identity is held by a deleted record", recordType);
                return null;
            }

            result.CreatedRecords.Add(created);
            Logger.LogDebug("Created {RecordType} from legacy record {LegacyId}", recordType, legacyId);
            return (created, true);
        }

        if (preserveStoredCorrelationId
            && existing.CorrelationId is { } storedCorrelationId
            && storedCorrelationId != Guid.Empty)
        {
            model.CorrelationId = storedCorrelationId;
        }

        model.Id = existing.Id;
        var updated = await repository.UpdateAsync(existing.Id, model, origin, ct);
        result.UpdatedRecords.Add(updated);
        Logger.LogDebug(
            "Updated existing {RecordType} {Id} from legacy record {LegacyId}", recordType, existing.Id, legacyId);
        return (updated, false);
    }

    /// <summary>
    /// Carries the attribution stored under the same legacy id forward onto a rebuilt model, then
    /// stamps whatever is still unattributed. A re-resolution that has since become ambiguous
    /// therefore cannot displace a stored link.
    /// </summary>
    /// <remarks>
    /// The source argument is the fallback for records carrying no
    /// <see cref="IDeviceAttributed.DataSource"/> of their own, so passing the model's own source
    /// here matches what a batch stamp resolves for the same record with no batch source at all.
    /// </remarks>
    protected static Task StampAttributionAsync(
        IPatientDeviceStamper stamper,
        IDeviceAttributed model,
        IDeviceAttributed? existing,
        IReadOnlyList<DeviceCategory> categories,
        CancellationToken ct)
    {
        model.PatientDeviceId ??= existing?.PatientDeviceId;
        return stamper.StampAsync([model], categories, model.DataSource, ct);
    }

    /// <summary>
    /// One table a legacy record decomposes into, for <see cref="PointAtStoredRecordsAsync"/>.
    /// <see cref="Resolve"/> is null for a table whose uuid-shaped legacy ids never go upstream as
    /// their prefix.
    /// </summary>
    protected readonly record struct KeyedTable(
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>> Held,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IEnumerable<UuidLegacyId>>>? Resolve,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IEnumerable<IV4Record>>> AdoptOwnIds,
        Func<IReadOnlyDictionary<Guid, string>, CancellationToken, Task<int>> AdoptByCorrelation,
        Func<IReadOnlyCollection<string>, string, CancellationToken, Task<IEnumerable<string>>> HeldOutsideSource);

    protected static KeyedTable Table<TRecord>(ILegacyKeyedRepository<TRecord> repository, bool resolvesUuidLegacyIds)
        where TRecord : class, IV4Record
        => new(
            repository.GetHeldLegacyIdsAsync,
            resolvesUuidLegacyIds ? repository.ResolveUuidLegacyIdsAsync : null,
            async (ids, ct) => await repository.AdoptOwnIdsAsync(ids, ct),
            repository.AdoptLegacyIdsByCorrelationAsync,
            repository.GetLegacyIdsHeldOutsideSourceAsync);

    /// <summary>
    /// Points each incoming document at the stored record it came from, before the legacy-id
    /// upserts run, by rewriting its id to that record's legacy id or giving a record with none the
    /// id it is named by. A copy of a record Nightscout write-back sent upstream then updates the
    /// record in place, or finds the user's deletion, instead of being stored a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document's <see cref="ProcessableDocumentBase.UpstreamIdentifier"/> is tried first: it is
    /// where write-back puts the record's own key, which survives every Nightscout version (see the
    /// Nightscout connector's <c>UpstreamIdentityJson</c>). It is taken only when it names a stored
    /// record, as a legacy id a live row or the user's deletion holds, or as a record's own uuid.
    /// Otherwise it is some other client's identifier, and the document stays under its <c>_id</c>,
    /// the key every earlier pull stored it by.
    /// </para>
    /// <para>
    /// The <c>_id</c> comes second, for a copy that lost its identifier or was written back before
    /// one was sent: a 24-hex id that is the prefix of a stored uuid-shaped legacy id is rewritten
    /// to that legacy id, and one that names a record's own uuid is adopted by that record.
    /// </para>
    /// <para>
    /// A document the Nightscout connector pulled that then names a record stored from any other
    /// source is that record's write-back echo: write-back sends every record not stored from the
    /// connector upstream, and the connector owns only what it stored itself. The caller writes
    /// nothing for an echo. Updating the record would re-attribute it to the connector, and
    /// write-back skips connector records, so a later edit in Nocturne would stay local and the
    /// next pull would restore the upstream copy over it. The identity pointing gave it is all it
    /// keeps.
    /// </para>
    /// </remarks>
    /// <param name="pulledFromNightscout">Whether a document came from the Nightscout connector.</param>
    /// <returns>The echoes among <paramref name="documents"/>.</returns>
    protected static async Task<IReadOnlySet<TDocument>> PointAtStoredRecordsAsync<TDocument>(
        IEnumerable<TDocument> documents,
        IReadOnlyList<KeyedTable> tables,
        Func<TDocument, bool> pulledFromNightscout,
        CancellationToken ct)
        where TDocument : ProcessableDocumentBase
    {
        var all = documents.ToList();
        await RewriteToStoredIdsAsync(all, tables, ct);
        return await EchoesAsync(all.Where(pulledFromNightscout).ToList(), tables, ct);
    }

    private static async Task<IReadOnlySet<TDocument>> EchoesAsync<TDocument>(
        List<TDocument> pulled, IReadOnlyList<KeyedTable> tables, CancellationToken ct)
        where TDocument : ProcessableDocumentBase
    {
        var echoes = new HashSet<TDocument>(ReferenceEqualityComparer.Instance);
        var ids = pulled.Select(d => d.Id).OfType<string>().Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return echoes;

        var foreign = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
            foreign.UnionWith(await table.HeldOutsideSource(ids, DataSources.NightscoutConnector, ct));

        foreach (var document in pulled)
        {
            if (document.Id is { } id && foreign.Contains(id))
                echoes.Add(document);
        }

        return echoes;
    }

    private static async Task RewriteToStoredIdsAsync<TDocument>(
        List<TDocument> documents, IReadOnlyList<KeyedTable> tables, CancellationToken ct)
        where TDocument : ProcessableDocumentBase
    {
        var pending = documents.ToList();

        var identified = pending.Where(d => d.UpstreamIdentifier is { Length: > 0 } i && i != d.Id).ToList();
        if (identified.Count > 0)
        {
            var identifiers = identified.Select(d => d.UpstreamIdentifier!).ToHashSet(StringComparer.Ordinal);
            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in tables)
                held.UnionWith(await table.Held(identifiers, ct));
            var adopted = await AdoptOwnIdsAsync(
                identifiers.Where(i => !held.Contains(i) && MongoObjectId.TryGetOwnIdRange(i, out _, out _)).ToHashSet(StringComparer.Ordinal),
                tables, ct);

            foreach (var document in identified)
            {
                if (!held.Contains(document.UpstreamIdentifier!) && !adopted.Contains(document.UpstreamIdentifier!))
                    continue;
                document.Id = document.UpstreamIdentifier;
                pending.Remove(document);
            }
        }

        var named = pending.Where(d => MongoObjectId.TryGetOwnIdRange(d.Id, out _, out _)).ToList();
        if (named.Count == 0)
            return;

        var prefixes = named.Select(d => d.Id!).Where(MongoObjectId.IsGuidPrefixShaped).ToHashSet(StringComparer.Ordinal);
        var legacyIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (prefixes.Count > 0)
        {
            foreach (var table in tables)
            {
                if (table.Resolve is null)
                    continue;
                foreach (var resolved in await table.Resolve(prefixes, ct))
                    legacyIds.TryAdd(resolved.WireId, resolved.LegacyId);
            }
        }

        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in named)
        {
            if (legacyIds.TryGetValue(document.Id!, out var legacyId))
                document.Id = legacyId;
            else
                own.Add(document.Id!);
        }

        await AdoptOwnIdsAsync(own, tables, ct);
    }

    /// <summary>
    /// Lets the stored record each of <paramref name="ids"/> names by its own uuid
    /// (<see cref="ILegacyKeyedRepository{TRecord}.AdoptOwnIdsAsync"/>) take that id as its legacy
    /// id, in whichever table holds it, then gives the same id to the records sharing its
    /// correlation id in every table: the group the legacy record decomposed into, which the
    /// legacy-id upserts that follow must all match.
    /// </summary>
    /// <returns>The ids a stored record took.</returns>
    private static async Task<IReadOnlySet<string>> AdoptOwnIdsAsync(
        IReadOnlyCollection<string> ids, IReadOnlyList<KeyedTable> tables, CancellationToken ct)
    {
        var adopted = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Count == 0)
            return adopted;

        var legacyIdByCorrelation = new Dictionary<Guid, string>();
        foreach (var table in tables)
        {
            foreach (var anchor in await table.AdoptOwnIds(ids, ct))
            {
                adopted.Add(anchor.LegacyId!);
                if (anchor.CorrelationId is { } correlationId && correlationId != Guid.Empty)
                    legacyIdByCorrelation.TryAdd(correlationId, anchor.LegacyId!);
            }
        }

        if (legacyIdByCorrelation.Count > 0)
        {
            foreach (var table in tables)
                await table.AdoptByCorrelation(legacyIdByCorrelation, ct);
        }

        return adopted;
    }

    /// <summary>
    /// System-attributes the writes made inside the scope, keeping the mutation audit log a human
    /// mutation trail.
    /// </summary>
    /// <remarks>
    /// Every legacy create reaches a decomposer's batch path — v1 and v3 normalize a lone record
    /// into a one-element array — so that path carries uploader ingestion (Loop, AAPS, xDrip,
    /// connectors, the demo seeder) at CGM sample rate. The audit interceptor drops
    /// system-attributed saves, and provenance for those rows lives on their own
    /// <see cref="IV4Record.DataSource"/>. The single-record path deliberately keeps caller
    /// attribution: it is reached from a genuine per-record edit, and since a legacy record
    /// persists only as its decomposed v4 rows, their audit rows are the whole trail of that edit.
    /// Connector re-syncs of the single path are system-attributed by the sync scope's own audit
    /// context rather than here. <see cref="ProfileDecomposer"/> takes no scope on either path: a
    /// profile persists only as its decomposed rows, so their audit rows are the whole trail of a
    /// user's profile edit, and an unchanged re-upsert writes nothing to audit.
    /// </remarks>
    protected static IDisposable SystemAttributedBatchWrites(IAuditContext auditContext)
        => SystemAuditScope.Push(auditContext);

    /// <summary>
    /// The batch twin of <see cref="UpsertByLegacyIdAsync"/>: a record whose legacy id is stored
    /// updates that row and lands in <see cref="DecompositionResult.UpdatedRecords"/>, so a resend
    /// through the batch path writes what the same resend through the single path writes.
    /// </summary>
    protected static async Task BulkUpsertAsync<TRecord>(
        IBulkUpsertRepository<TRecord> repository,
        List<TRecord> records,
        DecompositionResult result,
        WriteOrigin origin,
        CancellationToken ct)
        where TRecord : class
    {
        if (records.Count == 0)
            return;

        var written = await repository.BulkUpsertAsync(records, origin, ct);
        var updated = new HashSet<TRecord>(written.Updated, ReferenceEqualityComparer.Instance);
        result.CreatedRecords.AddRange(written.Where(r => !updated.Contains(r)));
        result.UpdatedRecords.AddRange(written.Updated);
        result.SkippedDeleted += written.SkippedDeleted;
    }

    protected static async Task BulkCreateAsync<TRecord>(
        IBulkCreateRepository<TRecord> repository,
        List<TRecord> records,
        DecompositionResult result,
        WriteOrigin origin,
        CancellationToken ct)
        where TRecord : class
    {
        if (records.Count == 0)
            return;

        var written = await repository.BulkCreateAsync(records, origin, ct);
        result.CreatedRecords.AddRange(written);
        result.SkippedDeleted += written.SkippedDeleted;
    }
}
