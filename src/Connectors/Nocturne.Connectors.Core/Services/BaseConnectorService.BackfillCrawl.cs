using Nocturne.Connectors.Core.Constants;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

public abstract partial class BaseConnectorService<TConfig> : IBackfillBudgeted
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
            new(Count + next.Count, next.Success, Earlier(Oldest, next.Oldest));
    }

    /// <inheritdoc />
    public TimeSpan BackfillBudget { get; set; } =
        ConnectorRunLimits.BackfillBudgetFor(ConnectorRunLimits.PerTenantSyncTimeout);

    /// <summary>When a background run's backfill crawls stop taking pages; null outside one.</summary>
    private DateTimeOffset? _backfillDeadline;

    private bool BackfillBudgetSpent => _backfillDeadline is { } deadline && Clock.GetUtcNow() >= deadline;

    /// <summary>The run's <see cref="ResumePointAsync"/> of each collection; null until the run asks for one.</summary>
    private Dictionary<string, DateTime>? _resumedFrom;

    /// <summary>
    ///     A family's resume point: the later of <paramref name="stored"/>, read from what the family
    ///     has stored, and the newest record its crawls have published (minus the catch-up overlap).
    /// </summary>
    /// <remarks>
    ///     A record can publish without leaving anything its family's stored watermark reads — a
    ///     device status carrying none of the APS, pump or uploader fields writes no snapshot — and
    ///     a family resuming from the stored watermark alone would then crawl its whole history on
    ///     every run. Everything below the point is stored or owed, so a catch-up that starts at or
    ///     below it may record how far it reached (<see cref="CatchUpAsync{T}"/>).
    /// </remarks>
    protected async Task<DateTime?> ResumePointAsync(
        string collection, DateTime? stored, CancellationToken cancellationToken)
    {
        var point = Later(stored, TryCalculateCatchUpSince(
            (await ResumeStateAsync(cancellationToken)).TryGetValue(ThroughKey(collection), out var through)
                ? through
                : null,
            collection));
        if (point is { } at)
            (_resumedFrom ??= [])[collection] = at;
        return point;
    }

    /// <summary>
    ///     The glucose family's lower bound: <see cref="ResumePointAsync"/> over its resume point on a
    ///     scheduled run, else the caller's <see cref="SyncRequest.From"/> as given.
    /// </summary>
    protected async Task<DateTime?> GlucoseFromAsync(
        SyncRequest request, string collection, CancellationToken cancellationToken) =>
        request.Resume.Scheduled
            ? await ResumePointAsync(collection, request.Resume.Point(ResumeFamily.Glucose), cancellationToken)
            : request.From;

    /// <summary>
    ///     Crawls a collection newest-first, publishing each page as it lands, then crawls what earlier
    ///     crawls of the collection left owed. Pages descend from the newest record, so whatever a
    ///     crawl never reached sits below the newest stored record, where an ordinary catch-up never
    ///     returns. Three entries of the source's crawl state carry the collection across runs, each
    ///     written in one update with whatever else changes alongside it:
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
    ///         <item>
    ///             How far the crawls reached, read by <see cref="ResumePointAsync"/>. An open-ended
    ///             full crawl sets it from its first page; an open-ended catch-up sets it, or moves it
    ///             on, only when it starts at or below what the crawls already covered.
    ///         </item>
    ///     </list>
    /// </summary>
    /// <remarks>
    ///     On a background run every crawl but the catch-up stops taking pages once
    ///     <see cref="BackfillBudget"/> has passed, after at least one page, and reports success: the
    ///     owed range stays recorded for the next run, and a run that timed out would fail the types
    ///     after this one and back the connector off.
    /// </remarks>
    /// <param name="pages">The collection's pages between two optional bounds, newest first.</param>
    /// <param name="timeOf">A record's time, or null for a record without a usable one.</param>
    /// <param name="pageSize">The records a full page holds; a page this long may not be the last.</param>
    /// <param name="cancellationToken">
    ///     For the crawl state read before the first page. The crawl's writes take none
    ///     (<see cref="WriteResumeStateAsync"/>).
    /// </param>
    protected async Task<PagedCrawlOutcome> CrawlAndPublishAsync<T>(
        string collection,
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T, DateTime?> timeOf,
        Func<T[], Task<bool>> publishAsync,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var crawl = new PageCrawl<T>(pages, timeOf, publishAsync, pageSize);
        await ForgetCrawlForResetAsync(collection, cancellationToken);
        var marks = await ResumeStateAsync(cancellationToken);
        DateTime? mark = marks.TryGetValue(collection, out var held) ? held : null;
        var gap = marks.TryGetValue(GapToKey(collection), out var gapTo)
                  && marks.TryGetValue(GapFromKey(collection), out var gapFrom)
            ? new Gap(gapFrom, gapTo)
            : null;

        if (from is not { } floor)
            return await CrawlFullAsync(collection, to, mark, gap, crawl, reachesNow: to is null);

        var outcome = await CatchUpAsync(
            collection, floor, to, gap, marks.TryGetValue(ThroughKey(collection), out var through) ? through : null, crawl);

        // A store that is failing right now shouldn't be hammered with the deep history too.
        if (outcome.Success && gap is not null)
            outcome = outcome.Plus(await ResumeGapAsync(collection, gap, crawl));
        if (outcome.Success && mark is not null)
            outcome = outcome.Plus(await CrawlFullAsync(collection, mark, mark, null, crawl, reachesNow: false));

        return outcome;
    }

    /// <summary>
    ///     A crawl with an open lower bound. It moves the mark down after every page, so a run that
    ///     ends anywhere leaves it on what it published, and on reaching the source's beginning clears
    ///     the mark and <paramref name="gap"/>, both covered.
    /// </summary>
    /// <param name="reachesNow">Whether the crawl starts at the newest record, and so sets how far the crawls reached.</param>
    private async Task<PagedCrawlOutcome> CrawlFullAsync<T>(
        string collection, DateTime? to, DateTime? mark, Gap? gap, PageCrawl<T> crawl, bool reachesNow)
    {
        var progress = new CrawlProgress();
        await PublishPagesAsync(crawl, null, to, progress, budgeted: true,
            afterPage: () =>
            {
                var changes = new Dictionary<string, DateTime?> { [collection] = progress.Lowest };
                if (reachesNow && progress.Pages == 1 && progress.Newest is { } newest)
                    changes[ThroughKey(collection)] = newest;
                return progress.Lowest is null ? Task.CompletedTask : WriteResumeStateAsync(changes, CancellationToken.None);
            });

        if (progress.Exhausted)
        {
            var changes = new Dictionary<string, DateTime?>();
            if (mark is not null || progress.Lowest is not null)
                changes[collection] = null;
            if (gap is not null)
                AddGap(changes, collection, null);
            await WriteResumeStateAsync(changes, CancellationToken.None);
        }

        return progress.Outcome;
    }

    /// <summary>
    ///     The bounded catch-up. A crawl that stops short owes the range between its floor and what
    ///     it published, merged into <paramref name="owed"/>. The same is written ahead once a full
    ///     page has published, or a second page has arrived, so a process killed mid-crawl has
    ///     recorded it too, and restored on finishing; a catch-up of one page that is not full writes
    ///     nothing until it finishes.
    /// </summary>
    /// <remarks>
    ///     An open-ended catch-up whose floor is at or below what the crawls already covered
    ///     (<paramref name="through"/>, or the run's <see cref="ResumePointAsync"/>) records the newest
    ///     record it has published as how far they reached, in the same update as every write of
    ///     what it owes and on finishing. At each such write <c>[lowest, newest]</c> is published,
    ///     <c>[from, lowest]</c> is owed, and everything below <c>from</c> was stored or owed before
    ///     the catch-up started, so everything below the reach mark is stored or owed whichever
    ///     write is the last. A catch-up that ends anywhere, finished, refused or cancelled, leaves
    ///     the next run resuming from what it published and working the rest through the budgeted
    ///     <see cref="ResumeGapAsync{T}"/>, rather than starting again from a stored watermark that
    ///     lags behind it, and so a reach mark a cursor reset cleared is set again.
    /// </remarks>
    private async Task<PagedCrawlOutcome> CatchUpAsync<T>(
        string collection, DateTime from, DateTime? to, Gap? owed, DateTime? through, PageCrawl<T> crawl)
    {
        var progress = new CrawlProgress();
        var wroteAhead = false;
        var reaches = to is null
            && from <= Later(through, _resumedFrom?.TryGetValue(collection, out var point) == true ? point : null);

        async Task WriteAsync(Dictionary<string, DateTime?> changes)
        {
            if (reaches && progress.Newest is { } newest && (through is null || newest > through))
                changes[ThroughKey(collection)] = newest;
            await WriteResumeStateAsync(changes, CancellationToken.None);
            if (changes.TryGetValue(ThroughKey(collection), out var reached))
                through = reached;
        }

        Task OweAsync() =>
            progress.Lowest is { } lowest
                ? WriteAsync(AddGap([], collection, new Gap(from, lowest).Hull(owed)))
                : Task.CompletedTask;

        async Task WriteAheadAsync()
        {
            if (wroteAhead)
                return;
            await OweAsync();
            wroteAhead = progress.Lowest is not null;
        }

        try
        {
            await PublishPagesAsync(crawl, from, to, progress, budgeted: false,
                beforePage: WriteAheadAsync,
                afterPage: () => progress.LastPageLength < crawl.PageSize ? Task.CompletedTask : WriteAheadAsync());
        }
        catch
        {
            await OweAsync();
            throw;
        }

        if (progress.Refused)
        {
            await OweAsync();
            return progress.Outcome;
        }

        await WriteAsync(wroteAhead ? AddGap([], collection, owed) : []);

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
                : WriteResumeStateAsync(
                    AddGap([], collection, lowest <= gap.From ? null : gap with { To = lowest }), CancellationToken.None);

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
            await WriteResumeStateAsync(AddGap([], collection, null), CancellationToken.None);
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
        Func<Task>? beforePage = null,
        Func<Task>? afterPage = null)
    {
        await foreach (var page in crawl.Pages(from, to))
        {
            if (progress.Pages > 0 && beforePage is not null)
                await beforePage();

            progress.Count += page.Length;
            if (!await crawl.PublishAsync(page))
            {
                progress.Refused = true;
                return;
            }

            progress.Pages++;
            progress.LastPageLength = page.Length;
            foreach (var at in page.Select(crawl.TimeOf).OfType<DateTime>())
            {
                progress.Lowest = Earlier(progress.Lowest, at);
                progress.Newest = Later(progress.Newest, at);
            }

            if (afterPage is not null)
                await afterPage();

            if (budgeted && BackfillBudgetSpent)
            {
                progress.Stopped = true;
                return;
            }
        }
    }

    private static DateTime? Earlier(DateTime? a, DateTime? b) => a is { } x && b is { } y ? (x < y ? x : y) : a ?? b;

    private static DateTime? Later(DateTime? a, DateTime? b) => a is { } x && b is { } y ? (x > y ? x : y) : a ?? b;

    private sealed record PageCrawl<T>(
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> Pages,
        Func<T, DateTime?> TimeOf,
        Func<T[], Task<bool>> PublishAsync,
        int PageSize);

    private sealed class CrawlProgress
    {
        public int Pages { get; set; }
        public int Count { get; set; }
        public int LastPageLength { get; set; }
        public DateTime? Lowest { get; set; }
        public DateTime? Newest { get; set; }
        public bool Refused { get; set; }
        public bool Stopped { get; set; }
        public bool Exhausted => !Refused && !Stopped;
        public PagedCrawlOutcome Outcome => new(Count, !Refused, Lowest);
    }

    /// <summary>A range a bounded crawl owes, kept in the crawl state beside the collection's mark.</summary>
    private sealed record Gap(DateTime From, DateTime To)
    {
        /// <summary>The smallest range covering this and <paramref name="other"/>.</summary>
        public Gap Hull(Gap? other) => other is null
            ? this
            : new(From < other.From ? From : other.From, To > other.To ? To : other.To);
    }

    /// <summary>
    ///     On a reset run's first crawl of <paramref name="collection"/>, clears what
    ///     <see cref="ResetClears"/> names of the collection's crawl state.
    /// </summary>
    private Task ForgetCrawlForResetAsync(string collection, CancellationToken cancellationToken)
    {
        if (_resetCrawled?.Add(collection) != true)
            return Task.CompletedTask;

        (string Key, ResumeKeyKind Kind)[] keys =
        [
            (collection, ResumeKeyKind.OwedMark),
            (GapFromKey(collection), ResumeKeyKind.OwedMark),
            (GapToKey(collection), ResumeKeyKind.OwedMark),
            (ThroughKey(collection), ResumeKeyKind.ReachMark),
        ];
        return WriteResumeStateAsync(keys
            .Where(key => ResetClears(key.Kind, repulled: true))
            .ToDictionary(key => key.Key, _ => (DateTime?)null),
            cancellationToken);
    }

    private static string GapFromKey(string collection) => $"{collection}.GapFrom";
    private static string GapToKey(string collection) => $"{collection}.GapTo";
    private static string ThroughKey(string collection) => $"{collection}.Through";

    private static Dictionary<string, DateTime?> AddGap(Dictionary<string, DateTime?> changes, string collection, Gap? gap)
    {
        changes[GapFromKey(collection)] = gap?.From;
        changes[GapToKey(collection)] = gap?.To;
        return changes;
    }
}
