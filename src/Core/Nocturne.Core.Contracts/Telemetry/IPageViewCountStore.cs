namespace Nocturne.Core.Contracts.Telemetry;

/// <summary>
/// <c>page_view_counts</c>: how many times each page of the web app was opened on each UTC day,
/// summed over every tenant and visitor. A row holds a day, a SvelteKit route id and a count, and
/// nothing that names a tenant, a person or a device.
/// </summary>
public interface IPageViewCountStore
{
    /// <summary>Adds each count to its day and route, creating the row when there is none.</summary>
    Task AddAsync(IReadOnlyCollection<PageViewCount> increments, CancellationToken ct);

    /// <summary>The rows from <paramref name="from"/> to <paramref name="to"/>, both inclusive, by day then route.</summary>
    Task<IReadOnlyList<PageViewCount>> ReadAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Deletes every row dated before <paramref name="cutoff"/>.</summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteBeforeAsync(DateOnly cutoff, CancellationToken ct);
}

/// <summary>How many times the page at <paramref name="RouteId"/> was opened on the UTC day <paramref name="Date"/>.</summary>
public sealed record PageViewCount(DateOnly Date, string RouteId, long Count);
