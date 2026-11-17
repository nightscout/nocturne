using System.Runtime.ExceptionServices;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>The decomposition of a batch of treatments, with what each treatment wrote.</summary>
internal sealed class TreatmentDecomposition : GroupedDecomposition
{
    private readonly Dictionary<int, Exception> _rejected = [];
    private readonly HashSet<int> _wroteNothing = [];

    /// <summary>
    /// The treatments refused for their data, by batch index, with the refusal: a
    /// <see cref="Core.Contracts.Treatments.TreatmentMappingException"/>, or a store refusal
    /// (<see cref="Infrastructure.Data.StoreRefusal"/>), in which case its other records may be stored.
    /// </summary>
    public IReadOnlyDictionary<int, Exception> Rejected => _rejected;

    internal void Reject(int index, Exception refusal) => _rejected.TryAdd(index, refusal);

    /// <summary>Whether the treatment at <paramref name="index"/> stores nothing by design: refused, or writing nothing of its own.</summary>
    public bool StoresNothing(int index) => _rejected.ContainsKey(index) || _wroteNothing.Contains(index);

    /// <summary>Rethrows the refusal of the treatment at <paramref name="index"/>, for a caller that wrote it alone.</summary>
    public void ThrowIfRejected(int index)
    {
        if (_rejected.TryGetValue(index, out var refusal))
            ExceptionDispatchInfo.Capture(refusal).Throw();
    }

    /// <summary>Marks a treatment that writes nothing of its own: one Nocturne does not store, or one held by a temp basal.</summary>
    internal void WritesNothing(int index) => _wroteNothing.Add(index);

    /// <summary>
    /// What the treatment at <paramref name="index"/> leaves stored, as a decomposition of its own: the
    /// records of its group, which a re-send of its legacy id in the same batch shares.
    /// </summary>
    public DecompositionResult Of(int index)
    {
        var of = new DecompositionResult { CorrelationId = index < Groups.Count ? Groups[index] : null };
        if (_rejected.ContainsKey(index) || _wroteNothing.Contains(index) || of.CorrelationId is not { } group)
            return of;

        of.CreatedRecords.AddRange(CreatedRecords.Where(r => GroupOf(r) == group));
        of.UpdatedRecords.AddRange(UpdatedRecords.Where(r => GroupOf(r) == group));
        of.UnchangedRecords.AddRange(UnchangedRecords.Where(r => GroupOf(r) == group));
        return of;
    }
}
