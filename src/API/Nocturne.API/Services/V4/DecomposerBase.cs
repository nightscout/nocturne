using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
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
    /// <returns>The persisted record, and whether it was inserted rather than updated.</returns>
    protected async Task<(TRecord Record, bool Created)> UpsertByLegacyIdAsync<TRecord>(
        ILegacyKeyedRepository<TRecord> repository,
        string? legacyId,
        TRecord model,
        DecompositionResult result,
        WriteOrigin origin,
        CancellationToken ct,
        Func<TRecord?, Task>? beforeWrite = null)
        where TRecord : class, IV4Record
    {
        var existing = legacyId is null ? null : await repository.GetByLegacyIdAsync(legacyId, ct);

        if (beforeWrite is not null)
            await beforeWrite(existing);

        var recordType = typeof(TRecord).Name;

        if (existing is null)
        {
            var created = await repository.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            Logger.LogDebug("Created {RecordType} from legacy record {LegacyId}", recordType, legacyId);
            return (created, true);
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

        result.CreatedRecords.AddRange(await repository.BulkCreateAsync(records, origin, ct));
    }
}
