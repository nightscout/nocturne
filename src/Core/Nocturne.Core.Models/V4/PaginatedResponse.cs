using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Generic paginated API response wrapper used by all V4 collection endpoints.
/// </summary>
/// <typeparam name="T">The type of items contained in <see cref="Data"/>.</typeparam>
/// <seealso cref="PaginationInfo"/>
public class PaginatedResponse<T>
{
    /// <summary>
    /// The page of items returned for the current request.
    /// </summary>
    public IEnumerable<T> Data { get; set; } = [];

    /// <summary>
    /// Pagination metadata describing the current window and, when available, the total result count.
    /// </summary>
    public PaginationInfo Pagination { get; set; } = new();
}

/// <summary>
/// Pagination metadata for a <see cref="PaginatedResponse{T}"/>.
/// </summary>
/// <param name="Limit">Maximum number of records returned per page (default 100).</param>
/// <param name="Offset">Zero-based index of the first record in the current page (default 0).</param>
/// <param name="Total">
/// Total number of records matching the query across all pages. Absent when the caller did not ask
/// for it and the page is full; see <see cref="ForPageAsync"/>.
/// </param>
public record PaginationInfo(
    int Limit = 100,
    int Offset = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Total = null)
{
    /// <summary>
    /// Builds the pagination for a page just read. The total is counted only when
    /// <paramref name="includeTotal"/> is set. A first page shorter than <paramref name="limit"/> already
    /// holds every record, so its total is its length and no count runs.
    /// </summary>
    public static async Task<PaginationInfo> ForPageAsync(
        int limit, int offset, int returned, bool includeTotal, Func<Task<int>> count)
    {
        if (includeTotal)
            return new PaginationInfo(limit, offset, await count());

        return new PaginationInfo(limit, offset, offset == 0 && returned < limit ? returned : null);
    }
}
