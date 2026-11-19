using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.Caching;

/// <summary>Reports every committed write's record types to <see cref="ILegacyReadCacheInvalidation"/>, whatever its origin.</summary>
internal sealed class LegacyReadCacheSubscriber(ILegacyReadCacheInvalidation legacyReads) : IRecordChangeSubscriber
{
    public RecordChangeStage Stage => RecordChangeStage.Invalidate;

    public Task OnChangedAsync(RecordsChanged batch, CancellationToken ct) =>
        legacyReads.WrittenAsync([.. batch.Changes.Select(change => change.RecordType).Distinct()]);
}
