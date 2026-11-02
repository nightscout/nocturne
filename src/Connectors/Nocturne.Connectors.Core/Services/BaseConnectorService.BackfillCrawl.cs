namespace Nocturne.Connectors.Core.Services;

public abstract partial class BaseConnectorService<TConfig>
{
    /// <summary>Outcome of one crawled collection, spanning every page of the crawl.</summary>
    /// <param name="Count">
    ///     The records handed to a publish, a refused page included, as
    ///     <see cref="RecordPublishOutcome"/> counts them.
    /// </param>
    /// <param name="Oldest">The oldest published record time, or null when none had one.</param>
    protected sealed record PagedCrawlOutcome(int Count, bool Success, DateTime? Oldest = null)
    {
        /// <summary>This outcome followed by <paramref name="next"/>, which ran only because this one succeeded.</summary>
        public PagedCrawlOutcome Plus(PagedCrawlOutcome next) =>
            new(Count + next.Count, next.Success, Oldest is { } a && next.Oldest is { } b ? (a < b ? a : b) : Oldest ?? next.Oldest);
    }

    /// <summary>When a background run's backfill crawls stop taking pages; null outside one.</summary>
    private DateTimeOffset? _backfillDeadline;

    private bool BackfillBudgetSpent => _backfillDeadline is { } deadline && Clock.GetUtcNow() >= deadline;

    protected Task<DateTime?> GetBackfillLowWaterMarkAsync(string collection) =>
        _publisher is { IsAvailable: true } p
            ? p.Metadata.GetBackfillLowWaterMarkAsync(ConnectorSource, collection)
            : Task.FromResult<DateTime?>(null);

    protected Task SetBackfillLowWaterMarkAsync(string collection, DateTime? mark) =>
        _publisher is { IsAvailable: true } p
            ? p.Metadata.SetBackfillLowWaterMarkAsync(ConnectorSource, collection, mark)
            : Task.CompletedTask;

    /// <summary>
    ///     Crawls a collection newest-first, publishing each page as it lands, then crawls what earlier
    ///     crawls of the collection left owed. Pages descend from the newest record, so whatever a
    ///     crawl never reached sits below the newest stored record, where an ordinary catch-up never
    ///     returns; two persisted records carry it across runs:
    ///     <list type="bullet">
    ///         <item>
    ///             The low-water mark, keyed by the collection: everything below it may be missing. A
    ///             full crawl (open lower bound) leaves it. Its resume is deliberately unbounded below,
    ///             so a raised or stale mark costs only redundant re-fetching, never a skipped region.
    ///         </item>
    ///         <item>
    ///             The gap (<see cref="Gap"/>): a bounded range a catch-up did not finish. It is kept
    ///             apart from the mark so a short failed catch-up neither costs a crawl of the whole
    ///             history nor replaces a deeper mark.
    ///         </item>
    ///     </list>
    /// </summary>
    /// <remarks>
    ///     On a background run every crawl but the catch-up stops taking pages once
    ///     <see cref="Constants.ConnectorRunLimits.BackfillBudget"/> has passed, after at least one
    ///     page, and reports success: the owed range stays recorded for the next run, and a run that
    ///     timed out would fail the types after this one and back the connector off.
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
        var crawl = new PageCrawl<T>(pages, oldestOf, publishAsync);
        var mark = await GetBackfillLowWaterMarkAsync(collection);
        var gap = await GetGapAsync(collection);

        if (from is not { } floor)
        {
            var full = await CrawlFullAsync(collection, to, mark, crawl);
            if (full.Exhausted && gap is not null)
                await SetGapAsync(collection, null);
            return full.Outcome;
        }

        var outcome = await CatchUpAsync(collection, floor, to, gap, crawl);

        // A store that is failing right now shouldn't be hammered with the deep history too.
        if (outcome.Success && gap is not null)
            outcome = outcome.Plus(await ResumeGapAsync(collection, gap, crawl));
        if (outcome.Success && mark is not null)
            outcome = outcome.Plus((await CrawlFullAsync(collection, mark, mark, crawl)).Outcome);

        return outcome;
    }

    /// <summary>
    ///     A crawl with an open lower bound. It moves the mark down after every page, so a run that
    ///     ends anywhere leaves it on what it published, and clears it on reaching the source's
    ///     beginning.
    /// </summary>
    private async Task<CrawlProgress> CrawlFullAsync<T>(
        string collection, DateTime? to, DateTime? mark, PageCrawl<T> crawl)
    {
        var progress = new CrawlProgress();
        await PublishPagesAsync(crawl, null, to, progress, budgeted: true,
            afterPage: () => progress.Lowest is { } lowest ? SetBackfillLowWaterMarkAsync(collection, lowest) : Task.CompletedTask);

        if (progress.Exhausted && (mark is not null || progress.Lowest is not null))
            await SetBackfillLowWaterMarkAsync(collection, null);

        return progress;
    }

    /// <summary>
    ///     The bounded catch-up. A crawl that stops short owes the range between its floor and what
    ///     it published, merged into <paramref name="owed"/>. Once a second page arrives the same is
    ///     written ahead, so a process killed mid-crawl has recorded it too, and restored on finishing;
    ///     a catch-up of one page writes nothing.
    /// </summary>
    private async Task<PagedCrawlOutcome> CatchUpAsync<T>(
        string collection, DateTime from, DateTime? to, Gap? owed, PageCrawl<T> crawl)
    {
        var progress = new CrawlProgress();
        var wroteAhead = false;

        Task OweAsync() =>
            progress.Lowest is { } lowest
                ? SetGapAsync(collection, new Gap(from, lowest).Hull(owed))
                : Task.CompletedTask;

        try
        {
            await PublishPagesAsync(crawl, from, to, progress, budgeted: false,
                beforeSecondPage: async () =>
                {
                    await OweAsync();
                    wroteAhead = progress.Lowest is not null;
                });
        }
        catch
        {
            await OweAsync();
            throw;
        }

        if (progress.Refused)
            await OweAsync();
        else if (wroteAhead)
            await SetGapAsync(collection, owed);

        return progress.Outcome;
    }

    /// <summary>
    ///     Crawls an owed range, narrowing it to what is still unpublished when the crawl stops short
    ///     and clearing it on reaching its floor.
    /// </summary>
    private async Task<PagedCrawlOutcome> ResumeGapAsync<T>(string collection, Gap gap, PageCrawl<T> crawl)
    {
        var progress = new CrawlProgress();

        Task NarrowAsync() =>
            progress.Lowest is not { } lowest || lowest >= gap.To
                ? Task.CompletedTask
                : SetGapAsync(collection, lowest <= gap.From ? null : gap with { To = lowest });

        try
        {
            await PublishPagesAsync(crawl, gap.From, gap.To, progress, budgeted: true);
        }
        catch
        {
            await NarrowAsync();
            throw;
        }

        if (progress.Exhausted)
            await SetGapAsync(collection, null);
        else
            await NarrowAsync();

        return progress.Outcome;
    }

    /// <summary>
    ///     Publishes pages until they run out, a publish is refused, or a budgeted crawl passes the
    ///     run's backfill deadline.
    /// </summary>
    private async Task PublishPagesAsync<T>(
        PageCrawl<T> crawl,
        DateTime? from,
        DateTime? to,
        CrawlProgress progress,
        bool budgeted,
        Func<Task>? beforeSecondPage = null,
        Func<Task>? afterPage = null)
    {
        await foreach (var page in crawl.Pages(from, to))
        {
            if (progress.Pages == 1 && beforeSecondPage is not null)
                await beforeSecondPage();

            progress.Count += page.Length;
            if (!await crawl.PublishAsync(page))
            {
                progress.Refused = true;
                return;
            }

            progress.Pages++;
            if (crawl.OldestOf(page) is { } oldest)
                progress.Lowest = oldest;

            if (afterPage is not null)
                await afterPage();

            if (budgeted && BackfillBudgetSpent)
            {
                progress.Stopped = true;
                return;
            }
        }
    }

    private sealed record PageCrawl<T>(
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> Pages,
        Func<T[], DateTime?> OldestOf,
        Func<T[], Task<bool>> PublishAsync);

    private sealed class CrawlProgress
    {
        public int Pages { get; set; }
        public int Count { get; set; }

        /// <summary>The oldest record time on the last published page; pages descend, so the crawl's lowest.</summary>
        public DateTime? Lowest { get; set; }

        public bool Refused { get; set; }
        public bool Stopped { get; set; }
        public bool Exhausted => !Refused && !Stopped;
        public PagedCrawlOutcome Outcome => new(Count, !Refused, Lowest);
    }

    /// <summary>
    ///     A range a bounded crawl owes, stored beside the collection's mark under two keys of the same
    ///     per-collection map.
    /// </summary>
    private sealed record Gap(DateTime From, DateTime To)
    {
        /// <summary>The smallest range covering this and <paramref name="other"/>.</summary>
        public Gap Hull(Gap? other) => other is null
            ? this
            : new(From < other.From ? From : other.From, To > other.To ? To : other.To);
    }

    private static string GapFromKey(string collection) => $"{collection}.GapFrom";
    private static string GapToKey(string collection) => $"{collection}.GapTo";

    private async Task<Gap?> GetGapAsync(string collection) =>
        await GetBackfillLowWaterMarkAsync(GapToKey(collection)) is { } to
        && await GetBackfillLowWaterMarkAsync(GapFromKey(collection)) is { } from
            ? new Gap(from, to)
            : null;

    /// <remarks>
    ///     The floor is written first: a write cut off between the two keys leaves a range at least as
    ///     wide as the one either write described.
    /// </remarks>
    private async Task SetGapAsync(string collection, Gap? gap)
    {
        await SetBackfillLowWaterMarkAsync(GapFromKey(collection), gap?.From);
        await SetBackfillLowWaterMarkAsync(GapToKey(collection), gap?.To);
    }
}
