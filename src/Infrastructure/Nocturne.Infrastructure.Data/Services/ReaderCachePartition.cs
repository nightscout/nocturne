using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// <see cref="IReaderCachePartition"/> derived from the RLS carriers on
/// <see cref="ICategoryReadContext"/>. Owners and unclamped members read every row and share
/// <see cref="Full"/>. A full-history share reads the categories in its CSV, so each distinct
/// category set is its own partition. A history-clamped reader sees a window that moves with the
/// clock and gets none; a share without full history is always clamped, so the history flag needs
/// no partition of its own.
/// </summary>
public sealed class ReaderCachePartition(ICategoryReadContext categoryReadContext) : IReaderCachePartition
{
    public const string Full = "full";

    private const string SharePrefix = "share=";

    public string? Current
    {
        get
        {
            if (categoryReadContext.IsHistoryClamped)
                return null;

            if (!categoryReadContext.IsShare)
                return Full;

            var categories = (categoryReadContext.VisibleCategoriesCsv ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            return SharePrefix + string.Join('+', categories);
        }
    }
}
