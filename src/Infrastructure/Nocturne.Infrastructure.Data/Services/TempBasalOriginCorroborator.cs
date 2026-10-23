using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Npgsql;
using NpgsqlTypes;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Settles the temp basals whose origin is a default (<see cref="TempBasalOriginProvenance"/>)
/// against the APS snapshots uploaded around their start (<see cref="ProposalsFor"/>), links the
/// snapshot that proposed each, and records the <see cref="TempBasalOriginEvidence"/> it rests on.
/// </summary>
/// <remarks>
/// <para>
/// Devicestatus and treatments arrive separately, so this runs on stored rows once both have
/// settled rather than at ingest. A row stored before evidence was recorded is classified first by
/// <see cref="TempBasalOriginProvenance.EvidenceOf(TempBasalOrigin, TempBasalOriginEvidence?, bool?, string?, string?, string?)"/>:
/// a stated origin is marked <see cref="TempBasalOriginEvidence.Reported"/> and left as it is, and a
/// default is replaced by its uploader's current default before it is judged.
/// </para>
/// <para>
/// A tenant's pending rows (<c>ix_temp_basals_origin_pending</c>) are walked in pages along
/// <c>(start_timestamp, id)</c>, and a judged row leaves that index, so a rerun reads only rows
/// written since. A row still settling is read and passed over; such rows are only those written in
/// the last settling delay, so the cost is bounded by what an import writes in that time. Each row
/// is written only if its <c>sys_updated_at</c> and evidence are those it was judged on, so an
/// upload that rewrote it meanwhile stands and is judged on a later run.
/// <c>sys_updated_at</c> is left alone, so v3 history does not resend every judged temp basal.
/// </para>
/// </remarks>
public static class TempBasalOriginCorroborator
{
    /// <summary>How far a snapshot or its enactment may be from a temp basal's start to judge it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How much later than the window a snapshot is read, for an enactment Trio repeats in the
    /// snapshots after the one that made it.
    /// </summary>
    private static readonly TimeSpan RepeatedEnactment = TimeSpan.FromMinutes(10);

    private const string PageSql = """
        SELECT id, start_timestamp, rate, device, app, data_source, origin, origin_evidence,
               additional_properties ->> 'automatic', aps_snapshot_id, sys_updated_at, legacy_id
        FROM temp_basals
        WHERE tenant_id = @tenant
          AND deleted_at IS NULL AND (origin_evidence IS NULL OR origin_evidence = 'Uploader')
          AND sys_updated_at < @settled
          AND (start_timestamp, id) > (@after_at, @after_id)
        ORDER BY start_timestamp, id
        LIMIT @size
        """;

    private const string ProposalSql = """
        SELECT DISTINCT ON (a.id) a.id, a.device, a.timestamp,
               COALESCE(a.enacted_json ->> 'timestamp', a.loop_json #>> '{enacted,timestamp}'),
               CASE WHEN a.enacted THEN a.enacted_rate END,
               COALESCE(a.suggested_json ->> 'rate',
                        a.loop_json #>> '{automaticDoseRecommendation,tempBasal,rate}',
                        a.loop_json #>> '{recommendedTempBasal,rate}')
        FROM unnest(@starts) AS w(start)
        JOIN aps_snapshots a
          ON a.timestamp >= w.start - @before AND a.timestamp <= w.start + @after
        WHERE a.tenant_id = @tenant AND a.deleted_at IS NULL
          AND a.timestamp >= @from AND a.timestamp <= @to
        ORDER BY a.id
        """;

    private const string WriteSql = """
        UPDATE temp_basals t
        SET origin = u.origin, origin_evidence = u.evidence, aps_snapshot_id = u.snapshot
        FROM unnest(@ids, @origins, @evidences, @snapshots, @read_at, @read_evidence)
            AS u(id, origin, evidence, snapshot, read_at, read_evidence)
        WHERE t.id = u.id AND t.tenant_id = @tenant AND t.deleted_at IS NULL
          AND t.sys_updated_at = u.read_at
          AND t.origin_evidence IS NOT DISTINCT FROM u.read_evidence
        """;

    private const string FreshSnapshotsSql = """
        SELECT date_bin('1 hour', timestamp, 'epoch'::timestamptz) AS hour, max(sys_updated_at)
        FROM aps_snapshots
        WHERE tenant_id = @tenant AND sys_updated_at > @after AND deleted_at IS NULL
        GROUP BY 1
        ORDER BY 1
        """;

    private const string ReopenSql = """
        WITH page AS (
            SELECT id, start_timestamp
            FROM temp_basals
            WHERE tenant_id = @tenant AND deleted_at IS NULL AND origin_evidence = 'Uncorroborated'
              AND start_timestamp >= @from AND start_timestamp <= @to
              AND (start_timestamp, id) > (@after_at, @after_id)
            ORDER BY start_timestamp, id
            LIMIT @size),
        reopened AS (
            UPDATE temp_basals t SET origin_evidence = 'Uploader'
            FROM page
            WHERE t.id = page.id AND t.origin_evidence = 'Uncorroborated'
            RETURNING 1)
        SELECT (SELECT count(*) FROM reopened), last.start_timestamp, last.id
        FROM (SELECT start_timestamp, id FROM page ORDER BY start_timestamp DESC, id DESC LIMIT 1) last
        """;

    /// <summary>What a snapshot proposed, and when.</summary>
    /// <param name="SnapshotId">The snapshot.</param>
    /// <param name="Timestamp">The snapshot's time.</param>
    /// <param name="EnactedAt">The time its enactment names, which Trio may repeat from an earlier snapshot.</param>
    /// <param name="EnactedRate">The rate it enacted; null when it enacted none.</param>
    /// <param name="SuggestedRate">The rate it suggested, enacted or not.</param>
    public sealed record Proposal(
        Guid SnapshotId, DateTime Timestamp, DateTime? EnactedAt, double? EnactedRate, double? SuggestedRate);

    public sealed record Verdict(TempBasalOrigin Origin, TempBasalOriginEvidence Evidence, Guid? SnapshotId);

    /// <summary>
    /// The proposals a temp basal starting at <paramref name="start"/> is judged against: those of
    /// its own device read within the window of its start. When it names no device, or its device
    /// uploaded none there, those of the one device that did, since a temp mirrored through another
    /// uploader (AAPS through Nightscout with no device, Trio through Tidepool) names no loop of its
    /// own; <c>OtherDevice</c> is then true. Snapshots that name no device count as one device there,
    /// but a null device is the absence of a name, so it never matches them as its own. With two or
    /// more devices there is no telling which loop it belongs to, so none.
    /// </summary>
    public static (IReadOnlyList<Proposal> Proposals, bool OtherDevice) ProposalsFor(
        string? device, DateTime start, IEnumerable<(string? Device, Proposal Proposal)> read)
    {
        var nearby = read
            .Where(p => p.Proposal.Timestamp >= start - Window && p.Proposal.Timestamp <= start + Window + RepeatedEnactment)
            .ToList();
        var own = device is null
            ? []
            : nearby.Where(p => string.Equals(p.Device, device, StringComparison.Ordinal)).ToList();
        if (own.Count > 0)
            return (own.Select(p => p.Proposal).ToList(), false);

        return nearby.Select(p => p.Device).Distinct(StringComparer.Ordinal).Count() == 1
            ? (nearby.Select(p => p.Proposal).ToList(), true)
            : ([], false);
    }

    /// <summary>
    /// Judges a temp basal against the proposals of its loop (<see cref="ProposalsFor"/>). One within
    /// <see cref="Window"/> of its start, by the snapshot's time or its enactment's, that enacted or
    /// suggested its rate makes it the loop's, linked to the nearest such. Proposals nearby that all
    /// named another rate mean the loop was running and the temp was set by hand. A proposal that
    /// named no rate (oref's "no temp required"), or an enactment repeated from outside the window, is
    /// no evidence either way. With no evidence, or no rate to compare, <paramref name="uploaderDefault"/> stands.
    /// </summary>
    /// <param name="otherDevice">
    /// The proposals are another device's (<see cref="ProposalsFor"/>), so a verdict rests on
    /// <see cref="TempBasalOriginEvidence.OtherDevice"/>.
    /// </param>
    public static Verdict Judge(
        TempBasalOrigin uploaderDefault, DateTime start, double? rate, IEnumerable<Proposal> proposals, bool otherDevice = false)
    {
        var rated = proposals
            .Select(p => (Proposal: p, Distance: DistanceFrom(p, start), Rates: RatesOf(p, start)))
            .Where(p => p.Distance <= Window && p.Rates.Length > 0)
            .ToList();
        if (rated.Count == 0 || rate is not { } r)
            return new Verdict(uploaderDefault, TempBasalOriginEvidence.Uncorroborated, null);

        var match = rated
            .Where(p => p.Rates.Any(proposed => TempBasalOriginProvenance.SameRate(proposed, r)))
            .OrderBy(p => p.Distance)
            .Select(p => p.Proposal)
            .FirstOrDefault();

        var evidence = otherDevice ? TempBasalOriginEvidence.OtherDevice : TempBasalOriginEvidence.Snapshot;
        return match is null
            ? new Verdict(TempBasalOrigin.Manual, evidence, null)
            : new Verdict(TempBasalOrigin.Algorithm, evidence, match.SnapshotId);
    }

    /// <summary>
    /// Judges every settled pending temp basal of the tenant <paramref name="db"/> is pinned to, a
    /// page at a time, and answers how many it wrote. Each page commits on its own, retried on a
    /// transient failure.
    /// </summary>
    /// <param name="db">A context pinned to <paramref name="tenantId"/>.</param>
    /// <param name="tenantId">The tenant whose temp basals to judge.</param>
    /// <param name="settledBefore">Rows written at or after this are left for a later run.</param>
    /// <param name="pageSize">Temp basals read per page.</param>
    /// <param name="pauseAfterWrite">The pause after a page that wrote.</param>
    /// <param name="pauseAfterRead">The pause after a page that wrote nothing.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<int> CorroborateTenantAsync(
        NocturneDbContext db,
        Guid tenantId,
        DateTime settledBefore,
        int pageSize,
        TimeSpan pauseAfterWrite,
        TimeSpan pauseAfterRead,
        CancellationToken ct)
    {
        var afterAt = DateTime.UnixEpoch;
        var afterId = Guid.Empty;
        var written = 0;

        while (true)
        {
            var page = await db.ExecuteInTransactionAsync(
                attemptCt => CorroboratePageAsync(db, tenantId, settledBefore, afterAt, afterId, pageSize, attemptCt),
                ct: ct);
            if (page is not { } done)
                return written;

            (afterAt, afterId) = (done.LastAt, done.LastId);
            written += done.Written;
            await Task.Delay(done.Written > 0 ? pauseAfterWrite : pauseAfterRead, ct);
        }
    }

    /// <summary>
    /// How far behind the last snapshot write a run has read the next one starts. A snapshot's
    /// <c>sys_updated_at</c> is stamped when it is saved, not when it commits, so a long import page,
    /// two uploaders or two replicas' clocks can commit one stamped before a write already read.
    /// Reading the overlap again only reopens temps that are judged the same.
    /// </summary>
    public static readonly TimeSpan CommitMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sends the <see cref="TempBasalOriginEvidence.Uncorroborated"/> temps of the tenant
    /// <paramref name="db"/> is pinned to back to pending when snapshots written after
    /// <paramref name="snapshotsWrittenAfter"/> now lie near them, so a later
    /// <see cref="CorroborateTenantAsync"/> judges them again. A Nightscout migration imports every
    /// treatment before any devicestatus, so its temps settle with no snapshot to judge them by.
    /// </summary>
    /// <returns>
    /// How many were reopened, and the latest <c>sys_updated_at</c> of the snapshots read, from
    /// which the next call starts, less <see cref="CommitMargin"/>; null when none were written.
    /// </returns>
    /// <remarks>
    /// The new snapshots are taken by the hour they fall in, and consecutive hours are joined, so one
    /// snapshot backfilled months back reopens only the temps of its own hour. Each span is walked
    /// through <c>ix_temp_basals_tenant_start_timestamp</c>; between runs it is minutes, and only an
    /// import makes it long.
    /// </remarks>
    public static async Task<(int Reopened, DateTime? SnapshotsWrittenThrough)> ReopenNearNewSnapshotsAsync(
        NocturneDbContext db,
        Guid tenantId,
        DateTime snapshotsWrittenAfter,
        int pageSize,
        CancellationToken ct)
    {
        var hours = await db.ExecuteInTransactionAsync(
            async attemptCt =>
            {
                await using var command = Command(db, FreshSnapshotsSql);
                command.Parameters.AddWithValue("tenant", tenantId);
                command.Parameters.AddWithValue("after", snapshotsWrittenAfter);
                var read = new List<(DateTime Hour, DateTime WrittenAt)>();
                await using var reader = await command.ExecuteReaderAsync(attemptCt);
                while (await reader.ReadAsync(attemptCt))
                    read.Add((reader.GetDateTime(0), reader.GetDateTime(1)));
                return read;
            },
            ct: ct);
        if (hours.Count == 0)
            return (0, null);

        var reopened = 0;
        foreach (var (from, to) in Spans(hours.Select(h => h.Hour)))
            reopened += await ReopenSpanAsync(db, tenantId, from, to, pageSize, ct);

        return (reopened, hours.Max(h => h.WrittenAt));
    }

    /// <summary>
    /// The start times a temp near a snapshot in each run of consecutive hours can have: a snapshot
    /// at <c>t</c> judges a temp starting from <c>t</c> less the repeated enactment and window to
    /// <c>t</c> plus the window.
    /// </summary>
    private static IEnumerable<(DateTime From, DateTime To)> Spans(IEnumerable<DateTime> orderedHours)
    {
        var hour = TimeSpan.FromHours(1);
        (DateTime First, DateTime Last)? run = null;
        foreach (var h in orderedHours)
        {
            if (run is { } r && h - r.Last <= hour)
            {
                run = (r.First, h);
                continue;
            }

            if (run is { } done)
                yield return (done.First - Window - RepeatedEnactment, done.Last + hour + Window);
            run = (h, h);
        }

        if (run is { } last)
            yield return (last.First - Window - RepeatedEnactment, last.Last + hour + Window);
    }

    private static async Task<int> ReopenSpanAsync(
        NocturneDbContext db, Guid tenantId, DateTime from, DateTime to, int pageSize, CancellationToken ct)
    {
        var afterAt = DateTime.UnixEpoch;
        var afterId = Guid.Empty;
        var reopened = 0;
        while (true)
        {
            var page = await db.ExecuteInTransactionAsync(
                async attemptCt =>
                {
                    await using var command = Command(db, ReopenSql);
                    command.Parameters.AddWithValue("tenant", tenantId);
                    command.Parameters.AddWithValue("from", from);
                    command.Parameters.AddWithValue("to", to);
                    command.Parameters.AddWithValue("after_at", afterAt);
                    command.Parameters.AddWithValue("after_id", afterId);
                    command.Parameters.AddWithValue("size", pageSize);
                    await using var reader = await command.ExecuteReaderAsync(attemptCt);
                    return await reader.ReadAsync(attemptCt)
                        ? ((int Reopened, DateTime LastAt, Guid LastId)?)((int)reader.GetInt64(0), reader.GetDateTime(1), reader.GetGuid(2))
                        : null;
                },
                ct: ct);
            if (page is not { } done)
                return reopened;

            reopened += done.Reopened;
            (afterAt, afterId) = (done.LastAt, done.LastId);
        }
    }

    private sealed record Row(
        Guid Id,
        DateTime Start,
        double? Rate,
        string? Device,
        string? EnteredBy,
        string? DataSource,
        TempBasalOrigin Origin,
        string? Evidence,
        bool? AutomaticFlag,
        Guid? SnapshotId,
        DateTime ReadAt,
        string? LegacyId);

    private static async Task<(int Written, DateTime LastAt, Guid LastId)?> CorroboratePageAsync(
        NocturneDbContext db,
        Guid tenantId,
        DateTime settledBefore,
        DateTime afterAt,
        Guid afterId,
        int pageSize,
        CancellationToken ct)
    {
        var rows = await ReadPageAsync(db, tenantId, settledBefore, afterAt, afterId, pageSize, ct);
        if (rows.Count == 0)
            return null;

        var proposals = await ReadProposalsAsync(db, tenantId, rows, ct);

        var verdicts = rows.Select(row => (Row: row, Verdict: Settle(row, proposals))).ToList();
        var written = await WriteAsync(db, tenantId, verdicts, ct);
        return (written, rows[^1].Start, rows[^1].Id);
    }

    private static Verdict Settle(Row row, List<(string? Device, Proposal Proposal)> proposals)
    {
        var stored = TempBasalOriginColumns.EvidenceOf(row.Evidence);
        var evidence = TempBasalOriginProvenance.EvidenceOf(
            row.Origin, stored, row.AutomaticFlag, row.EnteredBy, row.DataSource, row.LegacyId);
        if (evidence == TempBasalOriginEvidence.Reported)
            return new Verdict(row.Origin, TempBasalOriginEvidence.Reported, row.SnapshotId);

        // Its uploader named the snapshot whose algorithm set it.
        if (row.SnapshotId is { } linked)
            return new Verdict(TempBasalOrigin.Algorithm, TempBasalOriginEvidence.Snapshot, linked);

        var uploaderDefault = stored is null
            ? TempBasalOriginProvenance.UploaderDefault(row.EnteredBy, app: null)
            : row.Origin;
        var (loop, otherDevice) = ProposalsFor(row.Device, row.Start, proposals);
        return Judge(uploaderDefault, row.Start, row.Rate, loop, otherDevice);
    }

    private static TimeSpan DistanceFrom(Proposal proposal, DateTime start)
    {
        var distance = (proposal.Timestamp - start).Duration();
        return proposal.EnactedAt is { } enactedAt && (enactedAt - start).Duration() < distance
            ? (enactedAt - start).Duration()
            : distance;
    }

    private static double[] RatesOf(Proposal proposal, DateTime start)
    {
        var enacted = proposal.EnactedAt is { } enactedAt && (enactedAt - start).Duration() > Window
            ? null
            : proposal.EnactedRate;
        return new[] { enacted, proposal.SuggestedRate }.OfType<double>().ToArray();
    }

    private static async Task<List<Row>> ReadPageAsync(
        NocturneDbContext db,
        Guid tenantId,
        DateTime settledBefore,
        DateTime afterAt,
        Guid afterId,
        int pageSize,
        CancellationToken ct)
    {
        await using var command = Command(db, PageSql);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("settled", settledBefore);
        command.Parameters.AddWithValue("after_at", afterAt);
        command.Parameters.AddWithValue("after_id", afterId);
        command.Parameters.AddWithValue("size", pageSize);

        var rows = new List<Row>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Row(
                reader.GetGuid(0),
                reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                TempBasalOriginColumns.OriginOf(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : bool.TryParse(reader.GetString(8), out var flag) ? flag : null,
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.GetDateTime(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    private static async Task<List<(string? Device, Proposal Proposal)>> ReadProposalsAsync(
        NocturneDbContext db, Guid tenantId, List<Row> rows, CancellationToken ct)
    {
        await using var command = Command(db, ProposalSql);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.Add(new NpgsqlParameter("starts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            Value = rows.Select(r => r.Start).ToArray(),
        });
        command.Parameters.AddWithValue("before", Window);
        command.Parameters.AddWithValue("after", Window + RepeatedEnactment);
        command.Parameters.AddWithValue("from", rows[0].Start - Window);
        command.Parameters.AddWithValue("to", rows[^1].Start + Window + RepeatedEnactment);

        var proposals = new List<(string? Device, Proposal Proposal)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            proposals.Add((reader.IsDBNull(1) ? null : reader.GetString(1), new Proposal(
                reader.GetGuid(0),
                reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : ParseInstant(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : ParseRate(reader.GetString(5)))));
        }

        return proposals;
    }

    private static async Task<int> WriteAsync(
        NocturneDbContext db, Guid tenantId, List<(Row Row, Verdict Verdict)> verdicts, CancellationToken ct)
    {
        await using var command = Command(db, WriteSql);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("ids", verdicts.Select(v => v.Row.Id).ToArray());
        command.Parameters.AddWithValue("origins", verdicts.Select(v => v.Verdict.Origin.ToString()).ToArray());
        command.Parameters.AddWithValue("evidences", verdicts.Select(v => v.Verdict.Evidence.ToString()).ToArray());
        command.Parameters.Add(new NpgsqlParameter("snapshots", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = verdicts.Select(v => v.Verdict.SnapshotId is { } id ? (object)id : DBNull.Value).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("read_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            Value = verdicts.Select(v => v.Row.ReadAt).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("read_evidence", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = verdicts.Select(v => v.Row.Evidence is { } e ? (object)e : DBNull.Value).ToArray(),
        });

        return await command.ExecuteNonQueryAsync(ct);
    }

    // Plain ADO.NET on the context's connection, which carries its tenant: EF's raw SQL would read
    // the JSON paths' braces as format placeholders.
    private static NpgsqlCommand Command(NocturneDbContext db, string sql) =>
        new(sql, (NpgsqlConnection)db.Database.GetDbConnection())
        {
            Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction(),
        };

    private static DateTime? ParseInstant(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant.UtcDateTime
            : null;

    private static double? ParseRate(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : null;
}
