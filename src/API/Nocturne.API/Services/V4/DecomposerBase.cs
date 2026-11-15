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
/// The bulk create-or-update-by-<c>LegacyId</c> writes, the write onto a row resolved by its id, and
/// the correlation and attribution rules shared by the repository-backed decomposers.
/// </summary>
public abstract class DecomposerBase
{
    protected ILogger Logger { get; }

    protected DecomposerBase(ILogger logger) => Logger = logger;

    /// <summary>
    /// Writes <paramref name="model"/> onto <paramref name="stored"/>, a row its caller resolved by its
    /// own id rather than a legacy id, so it may carry none or another one for a bulk upsert to match
    /// it by; or creates <paramref name="model"/> when nothing is stored, counting a create the
    /// recreation guard refuses in <see cref="DecompositionResult.SkippedDeleted"/>. A model that
    /// resolved no device keeps the stored one, as a bulk upsert does
    /// (<see cref="IPatientDeviceLinked"/>).
    /// </summary>
    protected static async Task WriteOntoStoredAsync<TRecord>(
        IV4Repository<TRecord> repository,
        TRecord? stored,
        TRecord model,
        DecompositionResult result,
        WriteOrigin origin,
        CancellationToken ct)
        where TRecord : class, IV4Record
    {
        if (stored is null)
        {
            try
            {
                result.CreatedRecords.Add(await repository.CreateAsync(model, origin, ct));
            }
            catch (RecreationBlockedException)
            {
                result.SkippedDeleted++;
            }
            return;
        }

        if (model is IPatientDeviceLinked { PatientDeviceId: null } linked && stored is IPatientDeviceLinked storedLink)
            linked.PatientDeviceId = storedLink.PatientDeviceId;
        model.Id = stored.Id;
        result.UpdatedRecords.Add(await repository.UpdateAsync(stored.Id, model, origin, ct));
    }

    /// <summary>
    /// One table's <see cref="ILegacyKeyedRepository{TRecord}.GetCorrelationIdsByLegacyIdAsync"/>.
    /// </summary>
    protected delegate Task<IEnumerable<LegacyCorrelation>> StoredCorrelationLookup(
        IEnumerable<string> legacyIds, CancellationToken ct);

    /// <summary>
    /// The correlation id the stored rows of each of <paramref name="legacyIds"/> already carry,
    /// taken from the first of <paramref name="tables"/> that holds one.
    /// </summary>
    /// <remarks>
    /// A re-send keeps that id rather than minting a fresh one, so an unchanged re-send stays
    /// unchanged (<see cref="BulkUpsertAsync{TRecord}"/>), and a group row that carries no legacy id of
    /// its own is found only through the stored id. Stamping every sibling with the one id also converges
    /// a group that has forked.
    /// </remarks>
    protected static async Task<Dictionary<string, Guid>> GetStoredCorrelationIdsAsync(
        IEnumerable<string?> legacyIds, IReadOnlyList<StoredCorrelationLookup> tables, CancellationToken ct)
    {
        var correlationIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var ids = legacyIds.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return correlationIds;

        foreach (var table in tables)
        {
            foreach (var stored in await table(ids, ct))
                correlationIds.TryAdd(stored.LegacyId, stored.CorrelationId);
        }

        return correlationIds;
    }

    /// <summary>
    /// The correlation id to write <paramref name="legacyId"/>'s group under: the stored one from
    /// <see cref="GetStoredCorrelationIdsAsync"/>, else one minted and recorded in
    /// <paramref name="correlationIds"/> so the batch's duplicates of the legacy id share it.
    /// </summary>
    protected static Guid CorrelationIdFor(string? legacyId, Dictionary<string, Guid> correlationIds)
    {
        if (legacyId is null)
            return Guid.CreateVersion7();
        if (!correlationIds.TryGetValue(legacyId, out var correlationId))
            correlationIds[legacyId] = correlationId = Guid.CreateVersion7();
        return correlationId;
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

        /// <summary>
        /// A meal's carbs as write-back splits them off a meal stored as one record, under the hash of
        /// <see cref="PreBolusSplit.WriteBackCarbsId"/> of the meal's legacy id.
        /// </summary>
        PreBolusCarbs = 8,
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
    /// documents the Nightscout connector pulled, the only ones that carry that form, and gives it
    /// the time those documents span (<see cref="PulledWindow"/>); <paramref name="TakesUuids"/>
    /// offers it a canonical uuid besides the ObjectIds every lookup takes.
    /// </summary>
    protected readonly record struct WireIdResolver(
        Func<IReadOnlyCollection<string>, PulledWindow, CancellationToken, Task<IEnumerable<WireLegacyId>>> Resolve,
        bool PulledOnly,
        bool TakesUuids = false);

    /// <summary>
    /// The time the pulled documents of a batch span, widened by <see cref="PulledWindowMargin"/>. A
    /// copy carries its record's time, so this bounds the records a hashed id can name, which can
    /// only be found by hashing candidates (<see cref="ILegacyKeyedRepository{TRecord}.ResolveHashedLegacyIdsAsync"/>).
    /// </summary>
    protected readonly record struct PulledWindow(DateTime From, DateTime To);

    /// <summary>How far a pulled copy's time may lie from its record's, such as after an edit moved it.</summary>
    private static readonly TimeSpan PulledWindowMargin = TimeSpan.FromDays(1);

    protected static KeyedTable Table<TRecord>(ILegacyKeyedRepository<TRecord> repository, WireForms forms)
        where TRecord : class, IV4Record
    {
        var resolvers = new List<WireIdResolver>();
        if (forms.HasFlag(WireForms.UuidPrefix))
            resolvers.Add(new((ids, _, ct) => repository.ResolveUuidLegacyIdsAsync(ids, ct), PulledOnly: false));
        if (forms.HasFlag(WireForms.Hashed))
            resolvers.Add(new((ids, window, ct) => repository.ResolveHashedLegacyIdsAsync(ids, window.From, window.To, ct), PulledOnly: true));
        if (forms.HasFlag(WireForms.PreBolusCarbs))
            resolvers.Add(new((ids, window, ct) => repository.ResolvePreBolusCarbsLegacyIdsAsync(ids, window.From, window.To, ct), PulledOnly: true));
        if (forms.HasFlag(WireForms.KeyedOwnId))
            resolvers.Add(new((ids, _, ct) => repository.ResolveKeyedOwnIdsAsync(ids, ct), PulledOnly: false, TakesUuids: true));

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
        var pulledTimes = pulled.Where(d => d.Mills > 0).Select(d => d.Mills).ToList();
        PulledWindow? window = pulledTimes.Count == 0
            ? null
            : new PulledWindow(
                DateTimeOffset.FromUnixTimeMilliseconds(pulledTimes.Min()).UtcDateTime - PulledWindowMargin,
                DateTimeOffset.FromUnixTimeMilliseconds(pulledTimes.Max()).UtcDateTime + PulledWindowMargin);
        foreach (var table in tables)
        {
            foreach (var (resolve, pulledOnly, takesUuids) in table.Resolvers)
            {
                if (pulledOnly && window is null)
                    continue;
                var candidates = wireIds
                    .Where(n => !resolved.ContainsKey(n)
                                && (!pulledOnly || pulledNames.Contains(n))
                                && (takesUuids || MongoObjectId.IsObjectId(n)))
                    .ToHashSet(StringComparer.Ordinal);
                if (candidates.Count == 0)
                    continue;
                foreach (var hit in await resolve(candidates, window ?? default, ct))
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
    /// The audit interceptor drops system-attributed saves, so a decomposer applies this only to writes
    /// whose audit rows would not be a person's trail. Entries and device statuses arrive from uploaders
    /// at CGM sample rate and keep their provenance on each row's <see cref="IV4Record.DataSource"/>, so
    /// they are system-attributed, except a user's edit of one entry
    /// (<see cref="Entries.IEntryDecomposer.DecomposeAsync"/>). A legacy treatment or profile persists only as its decomposed
    /// rows, so their audit rows are the whole trail of a careportal entry or an edit: a live treatment
    /// keeps the caller's attribution and only a backfill is system-attributed, except the removal of a
    /// reclassified treatment's rows (<see cref="TreatmentDecomposer.DropAbsentAsync"/>), and
    /// <see cref="ProfileDecomposer"/> scopes only store removals during document replacement, since
    /// an unchanged re-upsert writes nothing to audit. Connector syncs are system-attributed by the
    /// sync scope's own audit context.
    /// </remarks>
    protected static IDisposable SystemAttributedBatchWrites(IAuditContext auditContext)
        => SystemAuditScope.Push(auditContext);

    protected static bool TryGetString(Dictionary<string, object> props, string key, out string value)
    {
        value = default!;
        if (!props.TryGetValue(key, out var obj))
            return false;

        if (obj is string s) { value = s; return true; }
        if (obj is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.String)
        { value = el.GetString()!; return true; }

        return false;
    }

    /// <summary>
    /// Parses an enum from one defined member name, ignoring case. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// also accepts numeric strings, comma lists that OR members together and padding, none of which
    /// an upload's string field can be trusted to mean.
    /// </summary>
    protected static bool TryParseDefinedName<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Create-or-update by legacy id: a record whose legacy id is stored updates that row and lands in
    /// <see cref="DecompositionResult.UpdatedRecords"/>, or, when the row already held it, in
    /// <see cref="DecompositionResult.UnchangedRecords"/>.
    /// </summary>
    protected static async Task<BulkWrite<TRecord>> BulkUpsertAsync<TRecord>(
        IBulkUpsertRepository<TRecord> repository,
        List<TRecord> records,
        DecompositionResult result,
        WriteOrigin origin,
        CancellationToken ct)
        where TRecord : class
    {
        if (records.Count == 0)
            return [];

        BulkWrite<TRecord> written;
        try
        {
            written = await repository.BulkUpsertAsync(records, origin, ct);
        }
        catch (Exception ex) when (CommittedWrite.TryGet<BulkWrite<TRecord>>(ex, out var committed))
        {
            Fold(committed, result);
            throw;
        }

        Fold(written, result);
        return written;
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

        BulkWrite<TRecord> written;
        try
        {
            written = await repository.BulkCreateAsync(records, origin, ct);
        }
        catch (Exception ex) when (CommittedWrite.TryGet<BulkWrite<TRecord>>(ex, out var committed))
        {
            Fold(committed, result);
            throw;
        }

        Fold(written, result);
    }

    private static void Fold<TRecord>(BulkWrite<TRecord> written, DecompositionResult result)
        where TRecord : class
    {
        result.CreatedRecords.AddRange(written.Created);
        result.UpdatedRecords.AddRange(written.Updated);
        result.UnchangedRecords.AddRange(written.Unchanged);
        result.SkippedDeleted += written.SkippedDeleted;
    }
}
