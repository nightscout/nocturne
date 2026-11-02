namespace Nocturne.Connectors.Core.Services;

public abstract partial class BaseConnectorService<TConfig>
{
    /// <summary>Outcome of one crawled collection, spanning every page of the crawl.</summary>
    protected sealed record PagedCrawlOutcome(int Count, bool Success)
    {
        /// <summary>This outcome followed by <paramref name="next"/>, which ran only because this one succeeded.</summary>
        public PagedCrawlOutcome Plus(PagedCrawlOutcome next) => new(Count + next.Count, next.Success);
    }

    protected Task<DateTime?> GetBackfillLowWaterMarkAsync(string collection) =>
        _publisher is { IsAvailable: true } p
            ? p.Metadata.GetBackfillLowWaterMarkAsync(ConnectorSource, collection)
            : Task.FromResult<DateTime?>(null);

    protected Task SetBackfillLowWaterMarkAsync(string collection, DateTime? mark) =>
        _publisher is { IsAvailable: true } p
            ? p.Metadata.SetBackfillLowWaterMarkAsync(ConnectorSource, collection, mark)
            : Task.CompletedTask;

    /// <summary>
    ///     Crawls a collection newest-first, publishing each page as it lands, then — when an
    ///     earlier crawl of the collection left a persisted low-water mark — resumes that
    ///     incomplete backfill below the mark. Pages descend from "now", so anything a killed
    ///     crawl never reached sits BELOW the newest stored record and an ordinary catch-up
    ///     never returns for it; the mark is what carries "history below X is still missing"
    ///     across runs that time out, process restarts and store failures.
    /// </summary>
    /// <remarks>
    ///     The resume crawl is deliberately UNBOUNDED below the mark, and that is load-bearing:
    ///     the mark's value only decides where re-crawling starts, never where it stops, so a
    ///     raised or stale mark can only cost redundant (idempotent) re-fetching — every
    ///     missing region below any surviving mark is eventually reached. Bounding the resume
    ///     (e.g. stopping at a previous mark, or persisting gap floors) would turn those same
    ///     states into data loss. The known cost: a failed bounded catch-up leaves a near-now
    ///     mark whose resume re-crawls the full history for a minutes-wide gap — rare, safe,
    ///     and preferred over a more fragile gap bookkeeping.
    /// </remarks>
    /// <param name="pages">The collection's pages between two optional bounds, newest first.</param>
    protected async Task<PagedCrawlOutcome> CrawlAndPublishAsync<T>(
        string collection,
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T[], DateTime?> oldestOf,
        Func<T[], Task<bool>> publishAsync)
    {
        var mark = await GetBackfillLowWaterMarkAsync(collection);

        var primary = await CrawlRangeAsync(
            collection, from, to, pages, oldestOf, publishAsync, fullCrawl: from is null);

        // Resume the incomplete backfill only when this cycle's primary crawl stored cleanly —
        // a store that is failing right now shouldn't be hammered with the deep history too.
        // A full primary crawl (open lower bound) already covers everything below the mark.
        if (mark is null || !primary.Success || from is null)
            return primary;

        var resume = await CrawlRangeAsync(
            collection, null, mark.Value,
            pages, oldestOf, publishAsync, fullCrawl: true);

        return new PagedCrawlOutcome(primary.Count + resume.Count, resume.Success);
    }

    /// <summary>
    ///     One newest-first crawl over a range. A page publish failure stops the crawl (pages
    ///     below a gap would strand it above the resume point) and records the low-water mark
    ///     so the next sync resumes there. Full crawls (open lower bound) also advance the mark
    ///     after every published page — crash protection for multi-hour histories — and clear
    ///     it on reaching the source's beginning; bounded catch-up crawls only ever raise it.
    /// </summary>
    private async Task<PagedCrawlOutcome> CrawlRangeAsync<T>(
        string collection,
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T[], DateTime?> oldestOf,
        Func<T[], Task<bool>> publishAsync,
        bool fullCrawl)
    {
        var count = 0;
        DateTime? lowestPublished = null;
        var success = true;

        try
        {
            await foreach (var page in pages(from, to))
            {
                count += page.Length;

                if (!await publishAsync(page))
                {
                    success = false;
                    break;
                }

                var pageOldest = oldestOf(page);
                if (pageOldest.HasValue)
                    lowestPublished = pageOldest;

                if (fullCrawl && lowestPublished.HasValue)
                    await SetBackfillLowWaterMarkAsync(collection, lowestPublished);
            }
        }
        catch
        {
            // A fetch failure or a cancelled run mid-crawl leaves the same gap a publish failure
            // does: record the resume point for what already published before surfacing it.
            await RaiseBackfillLowWaterMarkAsync(collection, lowestPublished);
            throw;
        }

        if (success && fullCrawl)
            await SetBackfillLowWaterMarkAsync(collection, null);
        else if (!success)
            await RaiseBackfillLowWaterMarkAsync(collection, lowestPublished);

        return new PagedCrawlOutcome(count, success);
    }

    /// <summary>
    ///     Raises the collection's low-water mark to <paramref name="candidate"/> — never lowers
    ///     it: a deeper mark from an earlier failure still describes missing history further down.
    /// </summary>
    private async Task RaiseBackfillLowWaterMarkAsync(string collection, DateTime? candidate)
    {
        if (!candidate.HasValue)
            return;

        var existing = await GetBackfillLowWaterMarkAsync(collection);
        if (existing is null || existing < candidate)
            await SetBackfillLowWaterMarkAsync(collection, candidate);
    }
}
