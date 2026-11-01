using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.DeviceStatuses;

/// <summary>The decomposition of a batch of device statuses, with what each status wrote.</summary>
/// <remarks>
/// Each write folds what it committed into it, a bulk write that throws after its commit included
/// (<see cref="Core.Contracts.V4.Repositories.CommittedWrite"/>), so a caller that passed it in reads
/// what the batch committed even when the batch throws part-way.
/// </remarks>
internal sealed class DeviceStatusDecomposition : DecompositionResult
{
    private IReadOnlyList<Guid> _groups = [];

    /// <summary>The status group each span the batch wrote belongs to.</summary>
    internal Dictionary<StateSpan, Guid> SpanOwners { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Starts the decomposition of statuses whose groups are <paramref name="groups"/>, in batch order.</summary>
    internal void Begin(IReadOnlyList<Guid> groups) => _groups = groups;

    /// <summary>
    /// Per status, in batch order: whether a record of its group was created or updated so far. A
    /// status whose every record was already stored as it decomposes wrote nothing. Empty until the
    /// batch has resolved its groups.
    /// </summary>
    public IReadOnlyList<bool> Wrote
    {
        get
        {
            var wrote = CreatedRecords.Concat(UpdatedRecords)
                .Select(r => r switch
                {
                    StateSpan span => SpanOwners.TryGetValue(span, out var owner) ? owner : (Guid?)null,
                    IV4Record record => record.CorrelationId,
                    DeviceStatusExtras extras => extras.CorrelationId,
                    _ => null,
                })
                .OfType<Guid>()
                .ToHashSet();
            return [.. _groups.Select(wrote.Contains)];
        }
    }
}
