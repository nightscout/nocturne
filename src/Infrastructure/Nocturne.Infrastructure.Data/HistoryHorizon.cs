using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// The stamp below which a history read may deliver rows: no transaction still in flight can commit
/// a row stamped under it, so a client's cursor never passes a row it has not been sent.
/// </summary>
/// <remarks>
/// A row is stamped before its transaction commits, and a long one (a bulk delete, an import) can
/// commit seconds after rows other transactions stamped later. A read in between returns those later
/// rows and moves the client past the stamps still to commit. The horizon is the earlier of the
/// database clock and the start of the oldest other transaction in the database that holds a
/// transaction id, less the <see cref="HistoryCommitMargin"/>, truncated to the millisecond a cursor
/// carries.
/// <para>
/// Every tenant-pinned connection carries its tenant in <c>application_name</c>
/// (<see cref="SessionTag"/>, written by <c>TenantConnectionInterceptor</c> and
/// <c>PinTenantAsync</c>), and a read waits only for its own tenant's writers and for untagged ones
/// (platform contexts, other roles' sessions are not seen at all). A writer that stamps with
/// <see cref="StampAsync"/> is covered exactly: it holds an id before its stamp exists, on the
/// database clock. A tracked save opens its connection before it stamps
/// (<c>NocturneDbContext.SaveChangesAsync</c>), so it is covered while its round trips to its first
/// write and the skew between the application and database clocks stay under the margin.
/// </para>
/// <para>
/// A writer of the reader's tenant is waited for however long it runs, since passing it over could
/// lose what it commits; one open longer than <see cref="MaxWriterAge"/> is logged, once per
/// transaction. An untagged writer that old is passed over, so a transaction left open cannot stall
/// every tenant's history, and is logged too. Setting <c>idle_in_transaction_session_timeout</c> on
/// the runtime role bounds both (see <c>docs/postgres</c>).
/// </para>
/// <para>
/// The horizon reads <c>pg_stat_activity</c>. Its <c>xact_start</c> is visible only for sessions of
/// the reader's own role, so a writer on another role (the migrator) is not waited for. It is empty
/// when <c>track_activities</c> is off, which leaves every horizon at the clock; a read logs that
/// once. It is read once per transaction and then held, so a horizon read inside an open transaction
/// would see the activity of the moment that transaction first looked; history reads run outside
/// one. The scan costs time in proportion to <c>max_connections</c>, once per history read.
/// </para>
/// <para>
/// Reads merged into one page must all be cut at the same horizon, or one cut lower than another
/// lets the merge pass rows the lower one held back; <see cref="Share"/> gives them one.
/// </para>
/// </remarks>
public static class HistoryHorizon
{
    internal static readonly TimeSpan MaxWriterAge = TimeSpan.FromMinutes(10);

    private const string TagPrefix = "nocturne-hist:";

    // The clock is read before the activity scan, so a writer that claims its id after the scan
    // stamps after the clock this returns.
    private static readonly string HorizonSql = $"""
        SELECT
            least(c.now, w.oldest) - make_interval(secs => @margin) AS "Value",
            coalesce(w.old_pids, ARRAY[]::int[]) AS "OldPids",
            coalesce(w.old_names, ARRAY[]::text[]) AS "OldNames",
            coalesce(w.old_starts, ARRAY[]::timestamptz[]) AS "OldStarts",
            coalesce(w.old_waited, ARRAY[]::boolean[]) AS "OldWaited",
            current_setting('track_activities')::boolean AS "Tracking",
            c.now AS "Now"
        FROM (SELECT clock_timestamp() AS now OFFSET 0) c
        CROSS JOIN LATERAL (
            SELECT
                min(a.xact_start) FILTER (WHERE a.own OR NOT a.old) AS oldest,
                array_agg(a.pid) FILTER (WHERE a.old) AS old_pids,
                array_agg(a.application_name) FILTER (WHERE a.old) AS old_names,
                array_agg(a.xact_start) FILTER (WHERE a.old) AS old_starts,
                array_agg(a.own) FILTER (WHERE a.old) AS old_waited
            FROM (
                SELECT
                    s.pid,
                    s.application_name,
                    s.xact_start,
                    coalesce(s.application_name = '{TagPrefix}' || current_setting('app.current_tenant_id', true), false) AS own,
                    s.xact_start <= c.now - make_interval(secs => {(int)MaxWriterAge.TotalSeconds}) AS old
                FROM pg_stat_activity s
                WHERE s.datname = current_database()
                  AND s.pid <> pg_backend_pid()
                  AND s.backend_type = 'client backend'
                  AND s.backend_xid IS NOT NULL
                  AND s.xact_start IS NOT NULL
                  AND (s.application_name NOT LIKE '{TagPrefix}%'
                       OR s.application_name = '{TagPrefix}' || current_setting('app.current_tenant_id', true))
            ) a
        ) w
        """;

    private const string StampSql = """
        SELECT clock_timestamp() AS "Value"
        FROM (SELECT pg_current_xact_id() AS xid OFFSET 0) x
        WHERE x.xid IS NOT NULL
        """;

    private static readonly AsyncLocal<Scope?> Current = new();

    private static readonly ConcurrentDictionary<(int Pid, DateTime Started), byte> ReportedWriters = new();

    private static int _reportedUntracked;

    /// <summary>The <c>application_name</c> a connection pinned to <paramref name="tenantId"/> carries.</summary>
    public static string SessionTag(Guid tenantId) => TagPrefix + tenantId;

    /// <summary>
    /// Opens a scope in which every history read is cut at the horizon the first of them reads.
    /// Inside an open scope, returns one that leaves it open.
    /// </summary>
    public static Scope Share()
    {
        if (Current.Value is { } open)
            return new Scope(open);

        var scope = new Scope(null);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    /// The exclusive upper bound for a history read on <paramref name="context"/>'s database, or
    /// <c>null</c> on a provider other than PostgreSQL.
    /// </summary>
    public static Task<DateTime?> GetAsync(DbContext context, ILogger logger, CancellationToken ct) =>
        Current.Value is { } scope
            ? scope.GetAsync(context, logger, ct)
            : ReadAsync(context, MarginOf(context), logger, ct);

    /// <summary>
    /// A stamp for the rows <paramref name="context"/>'s open transaction writes: the database clock,
    /// read once the transaction holds an id, so no horizon read before then is above it.
    /// </summary>
    public static async Task<DateTime> StampAsync(DbContext context, CancellationToken ct)
    {
        if (!context.Database.IsNpgsql())
            return NocturneDbContext.UtcNowAtStoredPrecision();
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A history stamp is taken inside a transaction");

        return await context.Database.SqlQueryRaw<DateTime>(StampSql).SingleAsync(ct);
    }

    internal static async Task<DateTime?> ReadAsync(
        DbContext context, TimeSpan margin, ILogger logger, CancellationToken ct)
    {
        if (!context.Database.IsNpgsql())
            return null;

        var read = await context.Database
            .SqlQueryRaw<HorizonRead>(HorizonSql, new Npgsql.NpgsqlParameter("margin", margin.TotalSeconds))
            .SingleAsync(ct);

        Report(read, logger);
        return DateTimeOffset.FromUnixTimeMilliseconds(read.Value.ToMills()).UtcDateTime;
    }

    private static void Report(HorizonRead read, ILogger logger)
    {
        if (!read.Tracking && Interlocked.Exchange(ref _reportedUntracked, 1) == 0)
            logger.LogWarning(
                "track_activities is off, so v3 history cannot see transactions still in flight and may miss rows they commit");

        if (ReportedWriters.Count > 1000)
            ReportedWriters.Clear();

        for (var i = 0; i < read.OldPids.Length; i++)
        {
            if (!ReportedWriters.TryAdd((read.OldPids[i], read.OldStarts[i]), 0))
                continue;

            logger.LogWarning(
                "Transaction on backend {Pid} ({ApplicationName}) has been open {Age}; v3 history {Handling}",
                read.OldPids[i],
                read.OldNames[i],
                read.Now - read.OldStarts[i],
                read.OldWaited[i]
                    ? "for its tenant waits for it to finish"
                    : "no longer waits for it and may miss what it commits");
        }
    }

    private static TimeSpan MarginOf(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
            ?.GetService<HistoryCommitMargin>()?.Value
        ?? HistoryCommitMargin.Default;

    private sealed class HorizonRead
    {
        public DateTime Value { get; init; }

        public int[] OldPids { get; init; } = [];

        public string[] OldNames { get; init; } = [];

        public DateTime[] OldStarts { get; init; } = [];

        public bool[] OldWaited { get; init; } = [];

        public bool Tracking { get; init; }

        public DateTime Now { get; init; }
    }

    /// <summary>The horizon shared by the history reads of one page.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly Scope? _outer;
        private readonly Lock _gate = new();
        private Task<DateTime?>? _horizon;

        internal Scope(Scope? outer) => _outer = outer;

        internal Task<DateTime?> GetAsync(DbContext context, ILogger logger, CancellationToken ct)
        {
            if (_outer is not null)
                return _outer.GetAsync(context, logger, ct);

            lock (_gate)
                return _horizon ??= ReadAsync(context, MarginOf(context), logger, ct);
        }

        /// <summary>
        /// Whether a stamp is below the horizon the scope's reads were cut at. Before any read has
        /// taken one, nothing was cut, and every stamp is.
        /// </summary>
        public bool Admits(long stampMills)
        {
            if (_outer is not null)
                return _outer.Admits(stampMills);

            return _horizon is not { IsCompletedSuccessfully: true, Result: { } horizon }
                || stampMills < horizon.ToMills();
        }

        public void Dispose()
        {
            if (_outer is null && Current.Value == this)
                Current.Value = null;
        }
    }
}

/// <summary>
/// How far a history read stays behind the newest stamp it could deliver, for writers that stamp on
/// the application clock before the database can see them (<see cref="HistoryHorizon"/>).
/// Configured as <c>PostgreSql:HistoryCommitMarginMilliseconds</c>.
/// </summary>
/// <remarks>
/// Two seconds covers a tracked save's round trips to its first write and ordinary clock skew, at
/// the cost of v3 history trailing writes by that long. A stack whose clients read history the
/// moment they write (the test and e2e stacks) sets it to zero.
/// </remarks>
public sealed record HistoryCommitMargin(TimeSpan Value)
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(2);
}
