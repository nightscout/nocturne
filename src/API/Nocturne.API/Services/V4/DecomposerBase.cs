using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
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
    /// <see cref="ProfileDecomposer"/> takes no scope, since an unchanged re-upsert writes nothing to
    /// audit. Connector syncs are system-attributed by the sync scope's own audit context.
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
