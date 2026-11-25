using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services;

public partial class NightscoutConnectorServiceBase<TConfig>
{
    /// <summary>
    ///     The vendor-state key holding the last open span a sync read again, so the next sync goes on
    ///     from the one after it.
    /// </summary>
    private const string OpenSpanRecheckKey = "OpenSpansRecheckedThrough";

    /// <summary>The most open spans one sync reads again, and so the most it looks up by <c>_id</c>.</summary>
    internal const int OpenSpansPerSync = MaxLookupsPerSync;

    /// <summary>
    ///     How soon a span with no end that started within <see cref="RecentlyStarted"/> is read again
    ///     once found still open, in place of <see cref="LookUpAgainAfter"/>: it is the override in force
    ///     now, the one an uploader is likeliest to end next.
    /// </summary>
    private static TimeSpan RunningLookUpAgainAfter => FullReconcileEvery;

    private static readonly TimeSpan RecentlyStarted = TimeSpan.FromDays(1);

    /// <summary>
    ///     Reads again, by id, the overrides and temporary targets this connector stored that are still
    ///     open or served as indefinite, and publishes the ones the source has since given a duration.
    ///     Loop ends an override by sending it again under the same id and <c>created_at</c>, which no
    ///     crawl or reconcile reads once newer records exist. The ones given a duration are published as
    ///     a crawl publishes, not under <see cref="ITreatmentPublisher.PublishRecentTreatmentsAsync"/>'s
    ///     rule, which takes a span stored with no fingerprint as it stands and so would leave it open.
    ///     A document marked
    ///     <c>isValid: false</c>, or confirmed gone (<see cref="ConfirmGoneAsync"/>), is deleted. A span
    ///     not deleted is not read again until <see cref="LookUpAgainAfter"/> has passed, or
    ///     <see cref="RunningLookUpAgainAfter"/> for one running now. Past <see cref="FewMissing"/>
    ///     spans the reads by <c>_id</c> did not find, none is looked up further and none is deleted. A
    ///     failed read changes nothing; a failed publish fails the sync and keeps the position.
    /// </summary>
    /// <param name="recent">
    ///     What the crawl's read returned, a source of ids known to be present for
    ///     <see cref="ConfirmGoneAsync"/>.
    /// </param>
    /// <returns>How many treatments were published, and whether the publish succeeded.</returns>
    private async Task<PagedCrawlOutcome> RecheckOpenSpansAsync(
        RecentRecords<Treatment>? recent, TConfig config, CancellationToken cancellationToken)
    {
        if (Publisher is not { IsAvailable: true } publisher)
            return new PagedCrawlOutcome(0, true);

        var after = await VendorStateAsync<StoredTreatmentKey>(OpenSpanRecheckKey, cancellationToken);
        var open = await publisher.Treatments.GetOpenSpanTreatmentsAsync(ConnectorSource, after, OpenSpansPerSync, cancellationToken);
        if (open.Count == 0 && after is not null)
            open = await publisher.Treatments.GetOpenSpanTreatmentsAsync(ConnectorSource, null, OpenSpansPerSync, cancellationToken);
        if (open.Count == 0)
            return new PagedCrawlOutcome(0, true);

        var now = Clock.GetUtcNow().UtcDateTime;
        var due = open.Where(span => !LookedUpRecently(span.LegacyId, now)).ToList();
        var found = new List<Treatment>();
        var gone = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (due.Count > 0)
                found = await ReadOpenSpansAsync(due, cancellationToken);

            var unresolved = due.Where(span => !found.Any(t => Names(t, span.LegacyId))).ToList();
            if (unresolved.Count > FewMissing)
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] {Missing} open overrides and temporary targets are missing from the source; looking none up and deleting none",
                    ConnectorSource, unresolved.Count);
            }
            else
            {
                foreach (var span in unresolved)
                    found.AddRange(await FindTreatmentByClientKeysAsync(span.LegacyId, span.At, cancellationToken));

                var missing = due.Where(span => !found.Any(t => Names(t, span.LegacyId))).ToList();
                var present = due
                    .Where(span => found.Any(t => Names(t, span.LegacyId)))
                    .Select(span => (span.LegacyId, span.At))
                    .Concat(recent?.Served.Select(s => (s.Key, s.Value)) ?? []);
                gone = await ConfirmGoneAsync(missing, present, found, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Reading open overrides and temporary targets again failed; reading them next sync",
                ConnectorSource);
            return new PagedCrawlOutcome(0, true);
        }
        catch (OperationCanceledException ex) when (ConnectorFaults.IsTimeout(ex, cancellationToken))
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Reading open overrides and temporary targets again timed out; reading them next sync",
                ConnectorSource);
            return new PagedCrawlOutcome(0, true);
        }

        var named = found
            .Where(t => due.Any(span => Names(t, span.LegacyId)))
            .GroupBy(t => t.Id)
            .SelectMany(copies => copies.Key is null ? copies : copies.Take(1))
            .ToList();
        var live = named.Where(t => t.IsValid != false).ToList();
        foreach (var span in due.Where(span => named.Any(t => Names(t, span.LegacyId)) && !live.Any(t => Names(t, span.LegacyId))))
            gone.Add(span.LegacyId);

        var ended = live.Where(t => t.Duration is > 0).ToList();
        if (ended.Count > 0)
        {
            foreach (var treatment in ended)
                treatment.DataSource = ConnectorSource;
            if (!await PublishByRouteAsync(ended, config, cancellationToken))
                return new PagedCrawlOutcome(0, false);
        }

        if (gone.Count > 0)
        {
            var deleted = await publisher.Treatments.DeleteTreatmentsAsync(ConnectorSource, gone, cancellationToken);
            _logger.LogInformation(
                "[{ConnectorSource}] Deleted {Records} records of {Treatments} open overrides and temporary targets the source deleted",
                ConnectorSource, deleted, gone.Count);
        }

        foreach (var span in due.Where(span => !gone.Contains(span.LegacyId)))
        {
            var runningNow = span.Running && now - span.At < RecentlyStarted
                && live.Any(t => Names(t, span.LegacyId) && t.Duration is not > 0);
            _lookups.Remember(LookupSource, span.LegacyId, now + (runningNow ? RunningLookUpAgainAfter : LookUpAgainAfter));
        }

        await SetVendorStateAsync<StoredTreatmentKey>(
            OpenSpanRecheckKey, open.Count < OpenSpansPerSync ? null : open[^1], cancellationToken);
        return new PagedCrawlOutcome(ended.Count, true);
    }

    /// <summary>
    ///     Whether <paramref name="treatment"/> is the document a span stored under
    ///     <paramref name="legacyId"/> came from: by its <c>_id</c>, its uploader's <c>id</c>, or the
    ///     <c>identifier</c> write-back gave it (<see cref="TreatmentExistsUpstreamAsync"/>).
    /// </summary>
    private static bool Names(Treatment treatment, string legacyId) =>
        treatment.Id == legacyId
        || TreatmentClientId.Of(treatment) == legacyId
        || (treatment.UpstreamIdentifier is { Length: > 0 } identifier
            && (identifier == legacyId || identifier == MongoObjectId.Coerce(legacyId)));

    /// <summary>
    ///     The documents behind <paramref name="open"/> that a find by <c>_id</c> returns: first all in
    ///     one read by <c>find[_id][$in]</c>, which skips Nightscout's default four-day window and
    ///     matches a string <c>_id</c> on every release but an ObjectId only from 15.0.7, then each
    ///     ObjectId-shaped span that read did not find by a plain <c>find[_id]</c>, which matches an
    ///     ObjectId on every release. A read that fails throws.
    /// </summary>
    private async Task<List<Treatment>> ReadOpenSpansAsync(
        IReadOnlyList<StoredTreatmentKey> open, CancellationToken cancellationToken)
    {
        const string operation = "FetchOpenSpans";
        var url = $"/api/v1/treatments.json?count={Math.Max(_currentConfig.MaxCount, open.Count)}"
                  + string.Concat(open.Select((span, i) => $"&find[_id][$in][{i}]={Uri.EscapeDataString(span.LegacyId)}"));
        var found = (await FetchDataAsync<Treatment[]>(url, operation, cancellationToken) ?? throw FetchFailed(operation)).ToList();

        foreach (var span in open.Where(span => IsObjectId(span.LegacyId) && !found.Any(t => Names(t, span.LegacyId))))
            found.AddRange(await FindTreatmentByObjectIdAsync(span.LegacyId, span.At, cancellationToken));

        return found;
    }

    /// <summary>
    ///     The spans of <paramref name="missing"/> to delete as gone from the source, which no find by id
    ///     found. As <see cref="DeleteTreatmentsGoneUpstreamAsync"/> does, a miss is trusted only once
    ///     the lookup finds a treatment known to be there with an id of the same shape. Each is then
    ///     looked for in the <c>created_at</c> range around its start (<see cref="ReadAroundAsync"/>):
    ///     15.0.7 and 15.0.8 cast a 24-hex value in every find by <c>_id</c>, so they cannot find a
    ///     document an older release stored under a string 24-hex <c>_id</c>.
    /// </summary>
    /// <param name="present">Ids the source is known to hold, each with its created_at.</param>
    /// <param name="found">Takes each document the range read finds.</param>
    private async Task<HashSet<string>> ConfirmGoneAsync(
        List<StoredTreatmentKey> missing,
        IEnumerable<(string LegacyId, DateTime At)> present,
        List<Treatment> found,
        CancellationToken cancellationToken)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (missing.Count == 0)
            return gone;

        var known = present.ToList();
        foreach (var kind in missing.GroupBy(span => IsObjectId(span.LegacyId)))
        {
            if (known.FirstOrDefault(p => IsObjectId(p.LegacyId) == kind.Key) is not { LegacyId: not null } canary)
                continue;

            if (!await TreatmentExistsUpstreamAsync(canary.LegacyId, canary.At, cancellationToken))
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] The source could not find a treatment it holds by id; deleting none",
                    ConnectorSource);
                return [];
            }

            foreach (var span in kind)
            {
                var copies = await ReadAroundAsync(span, cancellationToken);
                if (copies.Count > 0)
                    found.AddRange(copies);
                else
                    gone.Add(span.LegacyId);
            }
        }

        return gone;
    }

    /// <summary>
    ///     The documents with a <c>created_at</c> within <see cref="ReconcileReadMargin"/> of
    ///     <paramref name="span"/>'s start that name it, by <c>_id</c> in either case, <c>id</c> or
    ///     <c>identifier</c>.
    /// </summary>
    private async Task<List<Treatment>> ReadAroundAsync(StoredTreatmentKey span, CancellationToken cancellationToken)
    {
        var copies = new List<Treatment>();
        await foreach (var page in FetchCreatedAtPagesAsync<Treatment>(
            span.At - ReconcileReadMargin, span.At + ReconcileReadMargin, "treatments", t => t.CreatedAt,
            "ConfirmTreatmentGone", cancellationToken))
        {
            copies.AddRange(page.Where(t =>
                Names(t, span.LegacyId) || string.Equals(t.Id, span.LegacyId, StringComparison.OrdinalIgnoreCase)));
        }

        return copies;
    }
}
