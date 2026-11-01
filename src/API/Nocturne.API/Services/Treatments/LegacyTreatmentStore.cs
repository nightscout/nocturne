using System.Diagnostics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Treatments;

/// <summary>One way a client-sent treatment id may name a stored record.</summary>
internal abstract record LegacyRecordKey
{
    public sealed record RecordId(Guid Id) : LegacyRecordKey;

    public sealed record LegacyId(string Id) : LegacyRecordKey;

    public sealed record RecordIdPrefix(Guid Low, Guid High) : LegacyRecordKey;

    public sealed record LegacyIdUuidPrefix(string ObjectId) : LegacyRecordKey;

    public sealed record LegacyIdHash(string ObjectId) : LegacyRecordKey;

    /// <summary>
    /// A UUID is a record's own id or a client's legacy id. A 24-hex id is a client's legacy id, the
    /// prefix of a record's own id (the id reads serve), or the coerced legacy id an older create
    /// echoed.
    /// </summary>
    internal static IEnumerable<LegacyRecordKey> For(string id)
    {
        if (Guid.TryParse(id, out var guid))
        {
            yield return new RecordId(guid);
            yield return new LegacyId(id);
            yield break;
        }

        yield return new LegacyId(id);

        if (MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            yield return new RecordIdPrefix(low, high);
            yield return new LegacyIdUuidPrefix(id);
            yield return new LegacyIdHash(id);
        }
    }

    /// <summary>
    /// The record of <paramref name="found"/> <see cref="FindInAsync{T}"/> would return for this key: any
    /// one stored under a legacy id, else the first by id of those a prefix or range admits.
    /// </summary>
    internal IV4Record? Resolve(IReadOnlyList<IV4Record> found)
    {
        var named = found.Where(Names);
        return this is LegacyId
            ? named.FirstOrDefault()
            : named.OrderBy(r => r.Id.ToString("N"), StringComparer.Ordinal).FirstOrDefault();
    }

    private bool Names(IV4Record record) => this switch
    {
        RecordId k => record.Id == k.Id,
        LegacyId k => record.LegacyId == k.Id,
        RecordIdPrefix k => string.CompareOrdinal(record.Id.ToString("N"), k.Low.ToString("N")) >= 0
            && string.CompareOrdinal(record.Id.ToString("N"), k.High.ToString("N")) <= 0,
        LegacyIdUuidPrefix k => record.LegacyId is { } legacyId && HasUuidPrefix(legacyId, k.ObjectId),
        LegacyIdHash k => record.LegacyId is { } legacyId && MongoObjectId.Coerce(legacyId) == k.ObjectId,
        _ => throw new UnreachableException(),
    };

    /// <summary>
    /// Whether <paramref name="legacyId"/> is a UUID, dashed or not and in one case, whose 24-hex prefix
    /// is <paramref name="objectId"/>, as <see cref="ILegacyKeyedRepository{TRecord}.GetByLegacyIdUuidPrefixAsync"/> matches it.
    /// </summary>
    private static bool HasUuidPrefix(string legacyId, string objectId)
    {
        var dashed = string.Join('-', objectId[..8], objectId[8..12], objectId[12..16], objectId[16..20], objectId[20..]);
        var prefix = legacyId.Length switch { 36 => dashed, 32 => objectId, _ => null };
        return prefix is not null
            && Guid.TryParse(legacyId, out _)
            && (legacyId == legacyId.ToLowerInvariant() || legacyId == legacyId.ToUpperInvariant())
            && legacyId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<T?> FindInAsync<T>(ILegacyKeyedRepository<T> repository, CancellationToken ct)
        where T : class, IV4Record =>
        this switch
        {
            RecordId k => await repository.GetByIdAsync(k.Id, ct),
            LegacyId k => await repository.GetByLegacyIdAsync(k.Id, ct),
            RecordIdPrefix k => await repository.GetByGuidRangeAsync(k.Low, k.High, ct),
            LegacyIdUuidPrefix k => await repository.GetByLegacyIdUuidPrefixAsync(k.ObjectId, ct),
            LegacyIdHash k => await repository.GetByLegacyIdHashAsync(k.ObjectId, ct),
            _ => throw new UnreachableException(),
        };
}

/// <summary>What a client-sent treatment id names, as <see cref="LegacyTreatmentStore.ResolveAsync"/> resolved it.</summary>
internal abstract record LegacyResolvedTreatment
{
    /// <summary>The treatment served for it, or none.</summary>
    internal abstract Task<Treatment?> ReadAsync(CancellationToken ct);

    /// <summary>The treatment served for it under the id the decomposer upserts it on, or none.</summary>
    internal abstract Task<Treatment?> ForUpdateAsync(CancellationToken ct);

    /// <summary>
    /// Re-decomposes <paramref name="treatment"/> under the id the decomposer upserts it on and reads
    /// it back, or none when it serves nothing.
    /// </summary>
    internal abstract Task<Treatment?> UpdateAsync(Treatment treatment, CancellationToken ct);

    internal abstract Task<TreatmentDeletion?> DeleteAsync(CancellationToken ct);
}

/// <summary>
/// The stored side of the legacy treatment projection as a client-sent treatment id reaches it: the
/// records of <see cref="LegacyTreatmentTables.ByServedPrecedence"/> by <see cref="LegacyRecordKey"/>,
/// and once every key has missed, the state spans of <see cref="LegacyTreatmentTables.StateSpanTables"/>
/// in any form they are served in (<see cref="IV4ToLegacyProjectionService.GetProjectedStateSpanTreatmentAsync"/>).
/// A span is not a V4 record, so it has no keyed table.
/// </summary>
internal sealed class LegacyTreatmentStore(
    LegacyTreatmentRepositories repositories,
    IV4ToLegacyProjectionService projection,
    ITreatmentDecomposer decomposer,
    IStateSpanService stateSpans)
{
    private readonly LegacyTreatmentRepositories _repositories = repositories;
    private readonly IV4ToLegacyProjectionService _projection = projection;
    private readonly ITreatmentDecomposer _decomposer = decomposer;
    private readonly IStateSpanService _stateSpans = stateSpans;

    /// <summary>
    /// What a client-sent treatment id names, or none. A Note a treatment wrote beside another record
    /// or a span is served as part of it, so it resolves to it; see
    /// <see cref="LegacyTreatmentTables.NotesServedWithAnotherRecord"/>.
    /// </summary>
    internal async Task<LegacyResolvedTreatment?> ResolveAsync(string id, CancellationToken ct)
    {
        var spanId = id;
        if (await FindStoredAsync(LegacyRecordKey.For(id), id, ct) is { } stored)
        {
            if (stored.Record is not Note { LegacyId: { Length: > 0 } legacyId })
                return stored;

            if (await FindInAsync(ServingTables, new LegacyRecordKey.LegacyId(legacyId), id, ct) is { } holder)
                return holder;

            if (!await SpanHoldsAsync(legacyId, ct))
                return stored;

            spanId = legacyId;
        }

        return await _projection.GetProjectedStateSpanTreatmentAsync(spanId, ct) is { } served
            ? new ServedSpan(this, served, spanId, id)
            : null;
    }

    /// <summary>Whether the user deleted what a client-sent treatment id names.</summary>
    internal async Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct)
    {
        foreach (var table in LegacyTreatmentTables.ByServedPrecedence)
        {
            if (await table.Records.IsDeletedByUserAsync(_repositories, id, ct))
                return true;
        }

        return await _projection.IsStateSpanDeletedByUserAsync(id, ct);
    }

    /// <summary>
    /// Keys each created treatment whose id Nocturne served for a stored record or state span to its
    /// upsert key, so a client re-sending a document it read back (AAPS's nightscoutId, Loop's
    /// objectIdCache) replaces it, as Nightscout's upsert on <c>_id</c> does. Only ids shaped like a
    /// served one are looked up, and never by <see cref="LegacyRecordKey.LegacyIdHash"/>, which scans
    /// every legacy id. Each table is read once for all of them, the meal halves of what they name once
    /// per meal table, and the state spans once for those no record holds.
    /// </summary>
    internal async Task KeyToServedAsync(IReadOnlyList<Treatment> treatments, CancellationToken ct)
    {
        var ids = treatments.Select(t => t.Id).Where(MongoObjectId.IsGuidPrefixShaped).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return;

        var found = new Dictionary<ILegacyKeyedTreatmentTable, IReadOnlyList<IV4Record>>();
        foreach (var table in LegacyTreatmentTables.ByServedPrecedence)
            found[table] = await table.Records.FindServedAsync(_repositories, ids, ct);

        var named = new Dictionary<string, StoredRecord>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var stored = LegacyRecordKey.For(id)
                .Where(key => key is not LegacyRecordKey.LegacyIdHash)
                .SelectMany(key => LegacyTreatmentTables.ByServedPrecedence
                    .Select(table => (Table: table, Record: table.Records.Pick(found[table], key))))
                .FirstOrDefault(hit => hit.Record is not null);
            if (stored.Record is not null)
                named[id] = new StoredRecord(this, stored.Table, stored.Record, id);
        }

        var rekeyed = named.Values.Where(s => s.Record.LegacyId != s.Id).ToList();
        var halves = await MealHalvesAsync(rekeyed, ct);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stored in rekeyed)
            keys[stored.Id] = await UpsertKeyAsync(halves.GetValueOrDefault(stored) ?? [stored], ct);

        var spanKeys = await _projection.GetStateSpanTreatmentIdsAsync([.. ids.Where(id => !named.ContainsKey(id))], ct);
        foreach (var treatment in treatments)
        {
            if (treatment.Id is not { } id)
                continue;
            if (keys.TryGetValue(id, out var key) || (!named.ContainsKey(id) && spanKeys.TryGetValue(id, out key)))
                treatment.Id = key;
        }
    }

    /// <summary>
    /// The create response for a decomposed treatment. It carries the id every read serves for the
    /// treatment, so a client that keeps the response id (Loop's objectIdCache, AAPS's nightscoutId)
    /// can edit and delete by it later; the client's own id stays stored as the records' LegacyId.
    /// A treatment stored in none of the projected tables keeps the id it was sent with.
    /// </summary>
    internal async Task<StoredTreatment> ToCreatedAsync(Treatment treatment, DecompositionResult result, CancellationToken ct)
    {
        var records = result.StoredRecords.ToList();
        if (records.OfType<TempBasal>().FirstOrDefault() is { } tempBasal)
            return new StoredTreatment.Served(TempBasalToTreatmentMapper.ToTreatment(tempBasal), tempBasal.Id);

        if (records.OfType<StateSpan>().FirstOrDefault(LegacyTreatmentTables.ServesStateSpan) is { OriginalId: { } spanKey })
        {
            if (await _projection.GetProjectedStateSpanTreatmentAsync(spanKey, ct) is not { Id: { } spanId } span)
                return new StoredTreatment.Unnamed(treatment);

            treatment.Id = spanId;
            return new StoredTreatment.Named(treatment, span.Mills);
        }

        var served = LegacyTreatmentTables.ByServedPrecedence
            .Select(table => records.OfType<IV4Record>().FirstOrDefault(table.Model.IsInstanceOfType))
            .FirstOrDefault(record => record is not null);
        if (served is null)
            return new StoredTreatment.Unnamed(treatment);

        var servedAs = await ServedAsAsync(served, ct);
        treatment.Id = servedAs.Id.ToString();
        return new StoredTreatment.Named(treatment, servedAs.Mills);
    }

    /// <inheritdoc cref="ITreatmentStore.ReadBackAsync"/>
    internal async Task<IReadOnlyList<Treatment?>> ReadBackAsync(
        IReadOnlyList<StoredTreatment> stored, CancellationToken ct)
    {
        List<Guid> tempBasalIds = [.. stored.OfType<StoredTreatment.Served>().Select(s => s.RecordId).Distinct()];
        Dictionary<Guid, TempBasal> tempBasals = tempBasalIds.Count == 0
            ? new()
            : (await _repositories.TempBasals.GetByIdsAsync(tempBasalIds, ct)).ToDictionary(t => t.Id);

        var named = stored.OfType<StoredTreatment.Named>().ToList();
        var atInstants = named.Count == 0
            ? []
            : await _projection.GetProjectedTreatmentsAtAsync([.. named.Select(n => n.Mills)], ct);
        var projected = atInstants
            .Where(t => t.Id is not null)
            .DistinctBy(t => t.Id)
            .ToDictionary(t => t.Id!, StringComparer.Ordinal);

        var served = new List<Treatment?>(stored.Count);
        foreach (var treatment in stored)
        {
            served.Add(treatment switch
            {
                StoredTreatment.Served s => tempBasals.TryGetValue(s.RecordId, out var tempBasal)
                    ? TempBasalToTreatmentMapper.ToTreatment(tempBasal)
                    : null,
                StoredTreatment.Named { Created.Id: { } id } when projected.TryGetValue(id, out var found) => found,
                { Created.Id: { } id } => await ReadAsync(id, ct),
                _ => null,
            });
        }

        return served;
    }

    private static readonly IReadOnlyList<ILegacyKeyedTreatmentTable> ServingTables =
        [.. LegacyTreatmentTables.ByServedPrecedence.Where(table => table.Model != typeof(Note))];

    /// <summary>
    /// The stored record the keys name. Each key is tried against every table before the next, looser
    /// one, so an exact match always wins over a prefix or hash match. The records of one treatment
    /// share its legacy id, so the tables are tried in <see cref="LegacyTreatmentTables.ByServedPrecedence"/>,
    /// the order <see cref="ToCreatedAsync"/> names a treatment by: a legacy id resolves to the record
    /// the create returned, not to the note a record treatment with notes also writes.
    /// </summary>
    private async Task<StoredRecord?> FindStoredAsync(IEnumerable<LegacyRecordKey> keys, string id, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            if (await FindInAsync(LegacyTreatmentTables.ByServedPrecedence, key, id, ct) is { } stored)
                return stored;
        }

        return null;
    }

    private async Task<StoredRecord?> FindInAsync(
        IEnumerable<ILegacyKeyedTreatmentTable> tables, LegacyRecordKey key, string id, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            if (await table.Records.FindAsync(_repositories, key, ct) is { } record)
                return new StoredRecord(this, table, record, id);
        }

        return null;
    }

    private async Task<bool> SpanHoldsAsync(string treatmentId, CancellationToken ct) =>
        await _projection.GetStateSpanTreatmentIdAsync(treatmentId, ct) == treatmentId;

    /// <summary>
    /// The stored records served as one treatment with <paramref name="stored"/>: the bolus and the
    /// carb intake of a Meal Bolus, bolus first, which need not share a legacy id (a meal entered in
    /// Nocturne has none, a connector's carries one per half); else the record alone.
    /// </summary>
    private async Task<IReadOnlyList<StoredRecord>> ServedTogetherAsync(StoredRecord stored, CancellationToken ct) =>
        await MealHalvesAsync(stored.Table, stored.Record, ct) is { } halves
            ? [.. halves.Select(half => stored with { Table = half.Table, Record = half.Record })]
            : [stored];

    /// <summary>
    /// The bolus and the carb intake served as one Meal Bolus with <paramref name="record"/>, bolus
    /// first, or none when <paramref name="record"/> is not one of its halves: an extra carb of a
    /// correlation is served as a Carb Correction under its own id.
    /// </summary>
    private async Task<IReadOnlyList<(ILegacyKeyedTreatmentTable Table, IV4Record Record)>?> MealHalvesAsync(
        ILegacyKeyedTreatmentTable table, IV4Record record, CancellationToken ct)
    {
        if (!LegacyTreatmentTables.MealTables.Contains(table) || record.CorrelationId is not { } correlationId)
            return null;

        var byTable = await MealHalvesByTableAsync([correlationId], ct);
        return HalvesOf(byTable, correlationId, record);
    }

    /// <summary>The meal half each meal table holds per correlation id, one read per table.</summary>
    private async Task<List<(ILegacyKeyedTreatmentTable Table, IReadOnlyDictionary<Guid, IV4Record> Halves)>> MealHalvesByTableAsync(
        IReadOnlyCollection<Guid> correlationIds, CancellationToken ct)
    {
        var byTable = new List<(ILegacyKeyedTreatmentTable, IReadOnlyDictionary<Guid, IV4Record>)>();
        foreach (var mealTable in LegacyTreatmentTables.MealTables)
            byTable.Add((mealTable, await mealTable.Records.MealHalvesAsync(_repositories, correlationIds, ct)));
        return byTable;
    }

    private static IReadOnlyList<(ILegacyKeyedTreatmentTable Table, IV4Record Record)>? HalvesOf(
        IEnumerable<(ILegacyKeyedTreatmentTable Table, IReadOnlyDictionary<Guid, IV4Record> Halves)> byTable,
        Guid correlationId, IV4Record record)
    {
        var halves = byTable
            .Where(t => t.Halves.ContainsKey(correlationId))
            .Select(t => (t.Table, Record: t.Halves[correlationId]))
            .ToList();
        return halves.Count == LegacyTreatmentTables.MealTables.Count && halves.Any(h => h.Record.Id == record.Id)
            ? halves
            : null;
    }

    /// <summary>
    /// <see cref="ServedTogetherAsync"/> for each of <paramref name="stored"/> in a meal table, with one
    /// read per meal table for all of them.
    /// </summary>
    private async Task<Dictionary<StoredRecord, IReadOnlyList<StoredRecord>>> MealHalvesAsync(
        IReadOnlyList<StoredRecord> stored, CancellationToken ct)
    {
        var meal = stored
            .Where(s => LegacyTreatmentTables.MealTables.Contains(s.Table) && s.Record.CorrelationId is not null)
            .ToList();
        if (meal.Count == 0)
            return [];

        var byTable = await MealHalvesByTableAsync([.. meal.Select(s => s.Record.CorrelationId!.Value).Distinct()], ct);
        var together = new Dictionary<StoredRecord, IReadOnlyList<StoredRecord>>();
        foreach (var record in meal)
        {
            if (HalvesOf(byTable, record.Record.CorrelationId!.Value, record.Record) is { } halves)
                together[record] = [.. halves.Select(half => record with { Table = half.Table, Record = half.Record })];
        }

        return together;
    }

    /// <summary>
    /// The LegacyId the decomposer upserts a stored record on, with every record served beside it
    /// (<see cref="ServedTogetherAsync"/>). A native V4 row has none, so the decomposer could not
    /// match it and would insert a duplicate; the records without one are given the first one held,
    /// else the id the wire already shows. A record that holds another keeps it: its source re-syncs
    /// it by that id, and the decomposer finds a meal's carbs through their bolus.
    /// </summary>
    private async Task<string> UpsertKeyAsync(StoredRecord stored, CancellationToken ct) =>
        await UpsertKeyAsync(await ServedTogetherAsync(stored, ct), ct);

    /// <inheritdoc cref="UpsertKeyAsync(StoredRecord, CancellationToken)"/>
    /// <param name="records">A stored record and those served beside it, as <see cref="ServedTogetherAsync"/> finds them.</param>
    private async Task<string> UpsertKeyAsync(IReadOnlyList<StoredRecord> records, CancellationToken ct)
    {
        var key = records.Select(r => r.Record.LegacyId).FirstOrDefault(id => !string.IsNullOrEmpty(id))
            ?? MongoObjectId.FromGuid(records[0].Record.Id);

        foreach (var record in records.Where(r => string.IsNullOrEmpty(r.Record.LegacyId)))
        {
            record.Record.LegacyId = key;
            await record.Table.Records.UpdateAsync(_repositories, record.Record, ct);
        }

        return key;
    }

    private async Task<Treatment?> ProjectAsync(IV4Record record, CancellationToken ct)
    {
        if (record is TempBasal tempBasal)
            return TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        var served = await ServedAsAsync(record, ct);
        return await FindProjectedAsync(served.Mills, served.Id.ToString(), ct);
    }

    /// <summary>
    /// The record whose id names the treatment <paramref name="record"/> is served as: a carb intake
    /// that is half of a Meal Bolus is served under the bolus's id, any other record under its own.
    /// </summary>
    private async Task<IV4Record> ServedAsAsync(IV4Record record, CancellationToken ct) =>
        record is CarbIntake
            && await MealHalvesAsync(LegacyTreatmentTables.KeyedTableOf<CarbIntake>(), record, ct) is { } halves
                ? halves[0].Record
                : record;

    private async Task<Treatment?> FindProjectedAsync(long mills, string treatmentId, CancellationToken ct) =>
        (await _projection.GetProjectedTreatmentsAtAsync([mills], ct)).FirstOrDefault(t => t.Id == treatmentId);

    private async Task<Treatment?> ReadAsync(string id, CancellationToken ct) =>
        await ResolveAsync(id, ct) is { } resolved ? await resolved.ReadAsync(ct) : null;

    /// <summary>
    /// Deletes a served span with the Note its treatment wrote beside it before notes were kept on the
    /// span, which is served as part of the span and would otherwise outlive it.
    /// </summary>
    private async Task<TreatmentDeletion?> DeleteSpanAsync(Treatment served, string spanId, CancellationToken ct)
    {
        if (served.Id is not { } id || await _projection.GetStateSpanTreatmentIdAsync(spanId, ct) is not { } treatmentId)
            return null;

        await _decomposer.DeleteByLegacyIdAsync(treatmentId, WriteOrigin.Live, ct);
        return await _stateSpans.DeleteStateSpanAsync(id, ct) ? new TreatmentDeletion(served) : null;
    }

    private static InvalidOperationException ReadBackFailed(string id) =>
        new($"Treatment {id} did not read back after the update");

    /// <summary>A record of a keyed table, resolved from the client-sent id <paramref name="Id"/>.</summary>
    private sealed record StoredRecord(LegacyTreatmentStore Store, ILegacyKeyedTreatmentTable Table, IV4Record Record, string Id)
        : LegacyResolvedTreatment
    {
        internal override async Task<Treatment?> ReadAsync(CancellationToken ct) =>
            await Store.ProjectAsync(Record, ct)
                ?? await Store._projection.GetProjectedStateSpanTreatmentAsync(Id, ct);

        internal override async Task<Treatment?> ForUpdateAsync(CancellationToken ct)
        {
            if (await Store.ProjectAsync(Record, ct) is not { } existing)
                return null;

            existing.Id = await Store.UpsertKeyAsync(this, ct);
            return existing;
        }

        internal override async Task<Treatment?> UpdateAsync(Treatment treatment, CancellationToken ct)
        {
            if (await Store.ProjectAsync(Record, ct) is not { } existing)
                return null;

            TreatmentClientId.KeepStored(treatment, existing);

            var upsertKey = await Store.UpsertKeyAsync(this, ct);
            treatment.Id = upsertKey;
            (await Store._decomposer.DecomposeBatchAsync([treatment], WriteOrigin.Live, ct)).ThrowIfRejected(0);
            return await Store.ReadAsync(upsertKey, ct) ?? throw ReadBackFailed(Id);
        }

        internal override async Task<TreatmentDeletion?> DeleteAsync(CancellationToken ct)
        {
            var served = await Store.ProjectAsync(Record, ct);

            TreatmentDeletion? deletion = null;
            foreach (var record in await Store.ServedTogetherAsync(this, ct))
                deletion = await record.DeleteOneAsync(served, ct) ?? deletion;
            return deletion;
        }

        private async Task<TreatmentDeletion?> DeleteOneAsync(Treatment? served, CancellationToken ct)
        {
            // A record written beside another under one legacy id (a bolus wizard's calculation, a
            // correction's note) is deleted by it, so no sibling is left behind as a phantom treatment of
            // its own. The halves of a meal need not share one, so each is deleted by its own.
            if (Record.LegacyId is { Length: > 0 } legacyId)
            {
                var records = await Store._decomposer.DeleteByLegacyIdAsync(legacyId, WriteOrigin.Live, ct);
                var span = await Store.SpanHoldsAsync(legacyId, ct)
                    && await Store._projection.GetProjectedStateSpanTreatmentAsync(legacyId, ct) is { } spanServed
                        ? await Store.DeleteSpanAsync(spanServed, legacyId, ct)
                        : null;
                return span ?? (records > 0 ? new TreatmentDeletion(served) : null);
            }

            await Table.Records.DeleteAsync(Store._repositories, Record, ct);
            return new TreatmentDeletion(served);
        }
    }

    /// <summary>
    /// A state span served as <paramref name="Served"/>, found by <paramref name="SpanId"/> for the
    /// client-sent id <paramref name="Id"/>. The treatment id it was written under is read only by a
    /// write: a read never asks the gateway for it, a call sequence the span-mocking tests pin.
    /// </summary>
    private sealed record ServedSpan(LegacyTreatmentStore Store, Treatment Served, string SpanId, string Id)
        : LegacyResolvedTreatment
    {
        internal override Task<Treatment?> ReadAsync(CancellationToken ct) => Task.FromResult<Treatment?>(Served);

        internal override async Task<Treatment?> ForUpdateAsync(CancellationToken ct)
        {
            if (await Store._projection.GetStateSpanTreatmentIdAsync(SpanId, ct) is not { } upsertKey)
                return null;

            Served.Id = upsertKey;
            return Served;
        }

        /// <remarks>The decomposer upserts the span on the treatment id it was written under.</remarks>
        internal override async Task<Treatment?> UpdateAsync(Treatment treatment, CancellationToken ct)
        {
            if (Served.Id is not { } servedId
                || await Store._projection.GetStateSpanTreatmentIdAsync(SpanId, ct) is not { } upsertKey)
                return null;

            treatment.Id = upsertKey;
            (await Store._decomposer.DecomposeBatchAsync([treatment], WriteOrigin.Live, ct)).ThrowIfRejected(0);
            return await Store._projection.GetProjectedStateSpanTreatmentAsync(servedId, ct)
                ?? throw ReadBackFailed(Id);
        }

        internal override Task<TreatmentDeletion?> DeleteAsync(CancellationToken ct) =>
            Store.DeleteSpanAsync(Served, SpanId, ct);
    }
}
