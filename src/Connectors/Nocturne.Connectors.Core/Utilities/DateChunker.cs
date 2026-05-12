namespace Nocturne.Connectors.Core.Utilities;

public static class DateChunker
{
    public static IEnumerable<(DateTime From, DateTime To)> Chunk(
        DateTime from, DateTime to, TimeSpan chunkSize)
    {
        var current = from;
        while (current < to)
        {
            var chunkEnd = current + chunkSize;
            if (chunkEnd > to) chunkEnd = to;
            yield return (current, chunkEnd);
            current = chunkEnd;
        }

        // Edge case: from == to
        if (from == to)
            yield return (from, to);
    }
}
