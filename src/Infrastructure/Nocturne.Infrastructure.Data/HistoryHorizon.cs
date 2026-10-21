using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// The stamp below which a history read may deliver rows: no transaction still in flight can commit
/// a row stamped under it, so a client's cursor never passes a row it has not been sent.
/// </summary>
/// <remarks>
/// A row is stamped before its transaction commits, and a long one (a bulk delete) can commit
/// seconds after rows other transactions stamped later. A read in between returns those later rows
/// and moves the client past the stamps still to commit. The horizon is the earlier of the database
/// clock and the start of the oldest other transaction in the database that holds a transaction id,
/// less the <see cref="HistoryCommitMargin"/>, truncated to the millisecond a cursor carries.
/// <para>
/// A writer that takes its stamp with <see cref="StampAsync"/> is covered exactly: it holds an id
/// before its stamp exists, and the stamp is on the database clock. It also tags its session with its
/// tenant, so it holds back only that tenant's history, and it is waited for however long it runs.
/// Every other writer holds back every tenant's history while it runs. A tracked save stamps on the
/// application clock before its connection is even taken, so it is covered only while the pool wait,
/// the round trips before its first write and the skew between the two clocks (application behind
/// the database, or between replicas) together stay under the margin. An untagged writer open longer
/// than <see cref="MaxWriterAge"/> is not waited for, so a transaction left open cannot stall every
/// tenant's history; a read that passes one logs it.
/// </para>
/// <para>
/// The horizon reads <c>pg_stat_activity</c>. Its <c>xact_start</c> is visible only for sessions of
/// the reader's own role, so a writer on another role (the migrator) is not waited for. It is read
/// once per transaction and then held, so a horizon read inside an open transaction would see the
/// activity of the moment that transaction first looked; history reads run outside one. The scan
/// costs time in proportion to <c>max_connections</c>, once per history read.
/// </para>
/// <para>
/// Reads merged into one page must all be cut at the same horizon, or one cut lower than another
/// lets the merge pass rows the lower one held back; <see cref="Share"/> gives them one.
/// </para>
/// </remarks>
public static class HistoryHorizon
{
    internal static readonly TimeSpan MaxWriterAge = TimeSpan.FromMinutes(10);

    private const string Tag = "nocturne-hist:";

    // The clock is read before the activity scan, so a writer that claims its id after the scan
    // stamps after the clock this returns.
    private static readonly string HorizonSql = $"""
        SELECT least(c.now, w.oldest) - make_interval(secs => @margin) AS "Value", w.ignored AS "Ignored"
        FROM (SELECT clock_timestamp() AS now OFFSET 0) c
        CROSS JOIN LATERAL (
            SELECT
                min(a.xact_start) FILTER (WHERE a.tagged OR a.xact_start > c.now - make_interval(secs => {(int)MaxWriterAge.TotalSeconds})) AS oldest,
                count(*) FILTER (WHERE NOT a.tagged AND a.xact_start <= c.now - make_interval(secs => {(int)MaxWriterAge.TotalSeconds})) AS ignored
            FROM (
                SELECT s.xact_start, s.application_name = '{Tag}' || current_setting('app.current_tenant_id', true) AS tagged
                FROM pg_stat_activity s
                WHERE s.datname = current_database()
                  AND s.pid <> pg_backend_pid()
                  AND s.backend_type = 'client backend'
                  AND s.backend_xid IS NOT NULL
                  AND s.xact_start IS NOT NULL
                  AND (s.application_name NOT LIKE '{Tag}%'
                       OR s.application_name = '{Tag}' || current_setting('app.current_tenant_id', true))
            ) a
        ) w
        """;

    private static readonly string TagSql = $"""
        SELECT set_config('application_name', '{Tag}' || current_setting('app.current_tenant_id', true), true)
        WHERE coalesce(current_setting('app.current_tenant_id', true), '') <> ''
        """;

    private const string StampSql = """
        SELECT clock_timestamp() AS "Value"
        FROM (SELECT pg_current_xact_id() AS xid OFFSET 0) x
        WHERE x.xid IS NOT NULL
        """;

    private static readonly AsyncLocal<Scope?> Current = new();

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
    /// read once the transaction holds an id and its session carries its tenant's tag, so no horizon
    /// read before then for that tenant is above it.
    /// </summary>
    public static async Task<DateTime> StampAsync(DbContext context, CancellationToken ct)
    {
        if (!context.Database.IsNpgsql())
            return NocturneDbContext.UtcNowAtStoredPrecision();
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A history stamp is taken inside a transaction");

        await context.Database.ExecuteSqlRawAsync(TagSql, ct);
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

        if (read.Ignored > 0)
            logger.LogWarning(
                "History is not waiting for {Count} transaction(s) open longer than {MaxWriterAge}; rows they commit may be missed by v3 history clients",
                read.Ignored, MaxWriterAge);

        return DateTimeOffset.FromUnixTimeMilliseconds(HistoryPage.ToMilliseconds(read.Value)).UtcDateTime;
    }

    private static TimeSpan MarginOf(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
            ?.GetService<HistoryCommitMargin>()?.Value
        ?? HistoryCommitMargin.Default;

    private sealed class HorizonRead
    {
        public DateTime Value { get; init; }

        public long Ignored { get; init; }
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
                || stampMills < HistoryPage.ToMilliseconds(horizon);
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
