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
            LegacyUpsert<TRecord> written;
            try
            {
                written = await repository.CreateOrUpsertAsync(model, origin, ct);
            }
            catch (RecreationBlockedException)
            {
                // No live row carries the legacy id, so what holds it is the user's deletion.
                result.SkippedDeleted++;
                Logger.LogDebug("Skipped a {RecordType}: its identity is held by a deleted record", recordType);
                return null;
            }

            // A create its sync key matched to a stored row updated that row: it is reported as the
            // update it is, so it is announced as an edit and write-back looks for the copy upstream
            // holds before it writes.
            (written.Created ? result.CreatedRecords : result.UpdatedRecords).Add(written.Record);
            Logger.LogDebug(
                "{Outcome} {RecordType} from legacy record {LegacyId}",
                written.Created ? "Created" : "Updated by sync key", recordType, legacyId);
            return (written.Record, written.Created);
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
    /// The id forms beyond its legacy id and its own uuid that a table's records go upstream under,
    /// for <see cref="PointAtStoredRecordsAsync"/> to resolve back.
    /// </summary>
    [Flags]
    protected enum WireForms
    {
        None = 0,

        /// <summary>A uuid-shaped legacy id as its 24-hex prefix (<see cref="MongoObjectId.FromGuid"/>).</summary>
        UuidPrefix = 1,

        /// <summary>Any other non-ObjectId legacy id as its hash (<see cref="MongoObjectId.Coerce"/>).</summary>
        Hashed = 2,

        /// <summary>A record that carries a legacy id, under its own uuid or that uuid's 24-hex prefix.</summary>
        KeyedOwnId = 4,
    }

    /// <summary>One table a legacy record decomposes into, for <see cref="PointAtStoredRecordsAsync"/>.</summary>
    protected readonly record struct KeyedTable(
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>> Held,
        IReadOnlyList<WireIdResolver> Resolvers,
        Func<IReadOnlyCollection<string>, string, CancellationToken, Task<IEnumerable<UnkeyedOwnId>>> FindOwnIds,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IEnumerable<IV4Record>>> AdoptOwnIds,
        Func<IReadOnlyDictionary<Guid, string>, CancellationToken, Task<int>> AdoptByCorrelation,
        Func<IReadOnlyCollection<string>, string, CancellationToken, Task<IEnumerable<string>>> WriteBackMaySend);

    /// <summary>
    /// One <see cref="WireForms"/> lookup. <paramref name="PulledOnly"/> keeps it to the ids of
    /// documents the Nightscout connector pulled, the only ones that carry that form;
    /// <paramref name="TakesUuids"/> offers it a canonical uuid besides the ObjectIds every lookup
    /// takes.
    /// </summary>
    protected readonly record struct WireIdResolver(
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IEnumerable<WireLegacyId>>> Resolve,
        bool PulledOnly,
        bool TakesUuids = false);

    protected static KeyedTable Table<TRecord>(ILegacyKeyedRepository<TRecord> repository, WireForms forms)
        where TRecord : class, IV4Record
    {
        var resolvers = new List<WireIdResolver>();
        if (forms.HasFlag(WireForms.UuidPrefix))
            resolvers.Add(new(repository.ResolveUuidLegacyIdsAsync, PulledOnly: false));
        if (forms.HasFlag(WireForms.Hashed))
            resolvers.Add(new(repository.ResolveHashedLegacyIdsAsync, PulledOnly: true));
        if (forms.HasFlag(WireForms.KeyedOwnId))
            resolvers.Add(new(repository.ResolveKeyedOwnIdsAsync, PulledOnly: false, TakesUuids: true));

        return new(
            repository.GetHeldLegacyIdsAsync,
            resolvers,
            async (ids, source, ct) => await repository.FindUnkeyedOwnIdsAsync(ids, source, ct),
            async (ids, ct) => await repository.AdoptOwnIdsAsync(ids, ct),
            repository.AdoptLegacyIdsByCorrelationAsync,
            repository.GetLegacyIdsWriteBackMaySendAsync);
    }

    /// <summary>
    /// Where <see cref="PlanStoredIdentitiesAsync"/> points one document: the id it is stored under,
    /// whether a record with no legacy id takes that id first, and whether it is a write-back echo.
    /// <see cref="Fallback"/> is where it points instead when that record no longer can.
    /// </summary>
    protected sealed record PlannedIdentity(string? Id, bool Adopt, bool Echo, PlannedIdentity? Fallback = null);

    /// <summary>
    /// Points each incoming document at the stored record it came from, before the legacy-id
    /// upserts run: <see cref="PlanStoredIdentitiesAsync"/>, then
    /// <see cref="ApplyStoredIdentitiesAsync"/>.
    /// </summary>
    /// <returns>The echoes among <paramref name="documents"/>, which the caller stores nothing for.</returns>
    protected static async Task<IReadOnlySet<TDocument>> PointAtStoredRecordsAsync<TDocument>(
        IEnumerable<TDocument> documents,
        IReadOnlyList<KeyedTable> tables,
        Func<TDocument, bool> pulledFromNightscout,
        CancellationToken ct,
        Func<string, bool>? uploadedNameCanNameRecord = null)
        where TDocument : ProcessableDocumentBase
        => await ApplyStoredIdentitiesAsync(
            await PlanStoredIdentitiesAsync(documents, tables, pulledFromNightscout, ct, uploadedNameCanNameRecord),
            tables, ct);

    /// <summary>
    /// Works out, reading only, which stored record each document names: by its legacy id, or by a
    /// wire form of it (<see cref="WireForms"/>), or, for a record with no legacy id, by its own
    /// uuid, which that record then adopts as its legacy id. A copy of a record Nightscout
    /// write-back sent upstream then updates the record in place, or finds the user's deletion,
    /// instead of being stored a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document's <see cref="ProcessableDocumentBase.UpstreamIdentifier"/> is tried first: it is
    /// where write-back puts the record's key, which survives every Nightscout version (see the
    /// Nightscout connector's <c>UpstreamIdentityJson</c>). It is taken only when it names a stored
    /// record. Otherwise it is some other client's identifier, and the document's <c>_id</c> is
    /// tried the same way; a <c>_id</c> that names nothing stays as it is.
    /// </para>
    /// <para>
    /// A document the Nightscout connector pulled that names a record write-back may have sent
    /// upstream (<see cref="ILegacyKeyedRepository{TRecord}.GetLegacyIdsWriteBackMaySendAsync"/>) is
    /// that record's write-back echo. The caller writes nothing for it: updating the record would
    /// re-attribute it to the connector, whose records write-back skips, so a later edit in Nocturne
    /// would stay local and the next pull would restore the upstream copy over it. Any other record
    /// a pulled document names, such as one only a Nightscout migration imported, is the upstream's
    /// own, and the pull updates it.
    /// </para>
    /// </remarks>
    /// <param name="pulledFromNightscout">Whether a document came from the Nightscout connector.</param>
    /// <param name="uploadedNameCanNameRecord">
    /// Which ids of a document another uploader sent may name a stored record; by default its own
    /// uuid or that uuid's prefix (<see cref="MongoObjectId.TryGetOwnIdRange"/>).
    /// </param>
    protected static async Task<Dictionary<TDocument, PlannedIdentity>> PlanStoredIdentitiesAsync<TDocument>(
        IEnumerable<TDocument> documents,
        IReadOnlyList<KeyedTable> tables,
        Func<TDocument, bool> pulledFromNightscout,
        CancellationToken ct,
        Func<string, bool>? uploadedNameCanNameRecord = null)
        where TDocument : ProcessableDocumentBase
    {
        var all = documents.Distinct<TDocument>(ReferenceEqualityComparer.Instance).ToList();
        var pulled = new HashSet<TDocument>(all.Where(pulledFromNightscout), ReferenceEqualityComparer.Instance);
        var plans = new Dictionary<TDocument, PlannedIdentity>(ReferenceEqualityComparer.Instance);

        // Another uploader names a stored record only by the ids a v1 or v3 read serves: its legacy
        // id, which the upserts match unaided, or its own uuid or that uuid's prefix. A pulled copy
        // may carry any id write-back sends.
        var pulledNames = pulled.SelectMany(NamesOf).ToHashSet(StringComparer.Ordinal);
        var names = all.Where(d => !pulled.Contains(d)).SelectMany(NamesOf)
            .Where(uploadedNameCanNameRecord ?? (n => MongoObjectId.TryGetOwnIdRange(n, out _, out _)))
            .ToHashSet(StringComparer.Ordinal);
        names.UnionWith(pulledNames);
        if (names.Count == 0)
        {
            foreach (var document in all)
                plans[document] = new PlannedIdentity(document.Id, Adopt: false, Echo: false);
            return plans;
        }

        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
            held.UnionWith(await table.Held(names, ct));

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        // A document whose identifier is held needs nothing looked up under its _id.
        var unsettled = all
            .Where(d => !(IdentifierOf(d) is { } identifier && held.Contains(identifier)))
            .SelectMany(NamesOf)
            .Where(n => names.Contains(n) && !held.Contains(n))
            .ToHashSet(StringComparer.Ordinal);
        var wireIds = unsettled
            .Where(n => MongoObjectId.IsObjectId(n) || MongoObjectId.TryGetOwnIdRange(n, out _, out _))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            foreach (var (resolve, pulledOnly, takesUuids) in table.Resolvers)
            {
                var candidates = wireIds
                    .Where(n => !resolved.ContainsKey(n)
                                && (!pulledOnly || pulledNames.Contains(n))
                                && (takesUuids || MongoObjectId.IsObjectId(n)))
                    .ToHashSet(StringComparer.Ordinal);
                if (candidates.Count == 0)
                    continue;
                foreach (var hit in await resolve(candidates, ct))
                    resolved.TryAdd(hit.WireId, hit.LegacyId);
            }
        }

        var own = new Dictionary<string, bool>(StringComparer.Ordinal);
        var ownIds = unsettled
            .Where(n => !resolved.ContainsKey(n) && MongoObjectId.TryGetOwnIdRange(n, out _, out _))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            ownIds.ExceptWith(own.Keys);
            if (ownIds.Count == 0)
                break;
            foreach (var hit in await table.FindOwnIds(ownIds, DataSources.NightscoutConnector, ct))
                own.TryAdd(hit.WireId, hit.WriteBackMaySend);
        }

        var keyedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in pulled)
        {
            foreach (var name in NamesOf(document))
            {
                if (held.Contains(name))
                    keyedIds.Add(name);
                else if (resolved.TryGetValue(name, out var legacyId))
                    keyedIds.Add(legacyId);
            }
        }

        var maySend = new HashSet<string>(StringComparer.Ordinal);
        if (keyedIds.Count > 0)
        {
            foreach (var table in tables)
                maySend.UnionWith(await table.WriteBackMaySend(keyedIds, DataSources.NightscoutConnector, ct));
        }

        foreach (var document in all)
        {
            var isPulled = pulled.Contains(document);
            var byId = Target(document.Id, isPulled) ?? new PlannedIdentity(document.Id, Adopt: false, Echo: false);
            var byIdentifier = IdentifierOf(document) is { } identifier ? Target(identifier, isPulled) : null;
            plans[document] = byIdentifier is { Adopt: true }
                ? byIdentifier with { Fallback = byId }
                : byIdentifier ?? byId;
        }

        return plans;

        PlannedIdentity? Target(string? name, bool isPulled)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            if (held.Contains(name))
                return new PlannedIdentity(name, Adopt: false, Echo: isPulled && maySend.Contains(name));
            if (resolved.TryGetValue(name, out var legacyId))
                return new PlannedIdentity(legacyId, Adopt: false, Echo: isPulled && maySend.Contains(legacyId));
            if (own.TryGetValue(name, out var sendable))
                return new PlannedIdentity(name, Adopt: true, Echo: isPulled && sendable);
            return null;
        }
    }

    private static string? IdentifierOf(ProcessableDocumentBase document)
        => document.UpstreamIdentifier is { Length: > 0 } identifier && identifier != document.Id ? identifier : null;

    private static IEnumerable<string> NamesOf(ProcessableDocumentBase document)
    {
        if (IdentifierOf(document) is { } identifier)
            yield return identifier;
        if (document.Id is { Length: > 0 } id)
            yield return id;
    }

    /// <summary>
    /// Carries out <paramref name="plans"/>: gives the records they name by their own uuid those ids
    /// as legacy ids (<see cref="AdoptOwnIdsAsync"/>), then rewrites each document's id to the id it
    /// is stored under.
    /// </summary>
    /// <returns>The write-back echoes among the planned documents.</returns>
    protected static async Task<IReadOnlySet<TDocument>> ApplyStoredIdentitiesAsync<TDocument>(
        IReadOnlyDictionary<TDocument, PlannedIdentity> plans, IReadOnlyList<KeyedTable> tables, CancellationToken ct)
        where TDocument : ProcessableDocumentBase
    {
        var adopted = await AdoptOwnIdsAsync(
            plans.Values.Where(p => p.Adopt).Select(p => p.Id!).ToHashSet(StringComparer.Ordinal), tables, ct);

        var echoes = new HashSet<TDocument>(ReferenceEqualityComparer.Instance);
        foreach (var (document, planned) in plans)
        {
            var applied = planned.Adopt && !adopted.Contains(planned.Id!)
                ? planned.Fallback ?? new PlannedIdentity(document.Id, Adopt: false, Echo: false)
                : planned;
            document.Id = applied.Id;
            if (applied.Echo)
                echoes.Add(document);
        }

        return echoes;
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
        var updated = new HashSet<TRecord>(written.Updated, ReferenceEqualityComparer.Instance);
        result.CreatedRecords.AddRange(written.Where(r => !updated.Contains(r)));
        result.UpdatedRecords.AddRange(written.Updated);
        result.SkippedDeleted += written.SkippedDeleted;
    }
}
