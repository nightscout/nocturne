using Nocturne.API.Services.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>The decomposition of a batch of treatments, with what each treatment wrote.</summary>
internal sealed class TreatmentDecomposition : GroupedDecomposition
{
    private readonly Dictionary<int, string> _rejected = [];
    private readonly HashSet<int> _wroteNothing = [];

    /// <summary>The treatments refused for their data, by batch index, with why; nothing of them was written.</summary>
    public IReadOnlyDictionary<int, string> Rejected => _rejected;

    internal void Reject(int index, string reason) => _rejected.TryAdd(index, reason);

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
