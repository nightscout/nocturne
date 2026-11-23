using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Caching;

/// <summary>
/// Clears the cached first arrivals (<see cref="ImportedHistory"/>) when a committed write may have
/// moved one: glucose deleted or changed, a glucose write that names no records, or glucose created
/// with an arrival before the write, which is a restore (announced as a creation) or another
/// re-insert of an old row and can move a first arrival earlier. A write that only creates readings
/// stores them now, after every first arrival, so it moves none.
/// </summary>
internal sealed class GlucoseFirstArrivalsInvalidation(ITenantCache cache, TimeProvider time) : IRecordChangeSubscriber
{
    /// <summary>How far before the write a created reading's arrival may be and still be this write's.</summary>
    private static readonly TimeSpan StoredByThisWrite = TimeSpan.FromMinutes(1);

    public RecordChangeStage Stage => RecordChangeStage.Invalidate;

    public Task OnChangedAsync(RecordsChanged batch, CancellationToken ct)
    {
        var storedBefore = time.GetUtcNow().UtcDateTime - StoredByThisWrite;
        return batch.Changes.Any(c => c.RecordType == typeof(SensorGlucose)
                                      && (c.Deleted.Count > 0 || c.Updated.Count > 0 || !c.HasRecords
                                          || c.Created.OfType<SensorGlucose>().Any(r => r.CreatedAt < storedBefore)))
            ? cache.RemoveFamilyAsync(TenantCacheFamily.GlucoseFirstArrivals, ct)
            : Task.CompletedTask;
    }
}
