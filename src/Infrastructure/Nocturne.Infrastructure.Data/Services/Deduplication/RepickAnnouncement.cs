using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Services.Deduplication;

/// <summary>
/// Announces, after a deduplication commit, the record types whose shown copy it may have changed: a
/// merge, a link that joins a group, or a repaired flag hides or shows a row, which moves what reads
/// return (a device event re-picked moves a site change or sensor start by its match tolerance). The
/// announcement names types, not records, so no client is sent a re-pick as a new or changed record.
/// </summary>
/// <remarks>
/// A caller decides from counts it already has (groups merged, records joined, flags repaired), not from
/// the rows whose flag moved, so it can announce a type whose reads did not change. That costs an
/// evaluation and a cache clear; missing a type would leave a rule waiting on an anchor that moved.
/// Uncancellable for the reason <see cref="Core.Contracts.V4.ILegacyReadCacheInvalidation"/> takes no token.
/// </remarks>
internal static class RepickAnnouncement
{
    public static Task PublishAsync(IRecordChangePublisher? changes, IReadOnlyCollection<RecordType> repicked) =>
        repicked.Count == 0 || changes is null
            ? Task.CompletedTask
            : changes.PublishAsync(
                RecordsChanged.Written(repicked.Distinct().Select(V4RecordTables.ModelOf)), CancellationToken.None);
}
