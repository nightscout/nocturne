namespace Nocturne.Core.Models.Basal;

/// <summary>
/// Folds over <see cref="BasalSegment"/> streams. The primitive is the segment timeline; these
/// helpers cover the common reductions so call-sites stay short and don't reintroduce N+1 patterns
/// by accident.
/// </summary>
public static class BasalSegmentExtensions
{
    /// <summary>Total units delivered across the segment stream.</summary>
    public static async Task<double> SumUnitsAsync(
        this IAsyncEnumerable<BasalSegment> segments,
        CancellationToken ct = default)
    {
        double total = 0;
        await foreach (var seg in segments.WithCancellation(ct))
            total += seg.Units;
        return total;
    }

    /// <summary>Materialise into a list. Useful when the caller needs to fold the stream more than once.</summary>
    public static async Task<IReadOnlyList<BasalSegment>> ToListAsync(
        this IAsyncEnumerable<BasalSegment> segments,
        CancellationToken ct = default)
    {
        var list = new List<BasalSegment>();
        await foreach (var seg in segments.WithCancellation(ct))
            list.Add(seg);
        return list;
    }
}
