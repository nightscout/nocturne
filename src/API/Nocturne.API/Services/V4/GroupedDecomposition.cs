using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// The decomposition of a batch of legacy records, each written under a group (its correlation id),
/// with what each record wrote.
/// </summary>
/// <remarks>
/// Each write folds what it committed into it, a bulk write that throws after its commit included
/// (<see cref="Core.Contracts.V4.Repositories.CommittedWrite"/>), so a caller that passed it in reads
/// what the batch committed even when the batch throws part-way.
/// </remarks>
internal abstract class GroupedDecomposition : DecompositionResult
{
    private IReadOnlyList<Guid> _groups = [];

    /// <summary>The group each span the batch wrote belongs to.</summary>
    internal Dictionary<StateSpan, Guid> SpanOwners { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Starts the decomposition of records whose groups are <paramref name="groups"/>, in batch order.</summary>
    internal void Begin(IReadOnlyList<Guid> groups) => _groups = groups;

    /// <summary>The groups of the batch's records, in batch order. Empty until the batch has resolved them.</summary>
    internal IReadOnlyList<Guid> Groups => _groups;

    /// <summary>The group a record of this decomposition was written under, or none.</summary>
    internal Guid? GroupOf(object record) => record switch
    {
        StateSpan span => SpanOwners.TryGetValue(span, out var owner) ? owner : null,
        IV4Record v4 => v4.CorrelationId,
        DeviceStatusExtras extras => extras.CorrelationId,
        _ => null,
    };

    /// <summary>
    /// Per record, in batch order: whether a record of its group was created or updated so far. A
    /// record whose every row was already stored as it decomposes wrote nothing. Empty until the
    /// batch has resolved its groups.
    /// </summary>
    public IReadOnlyList<bool> Wrote
    {
        get
        {
            var wrote = CreatedRecords.Concat(UpdatedRecords).Select(GroupOf).OfType<Guid>().ToHashSet();
            return [.. _groups.Select(wrote.Contains)];
        }
    }
}
