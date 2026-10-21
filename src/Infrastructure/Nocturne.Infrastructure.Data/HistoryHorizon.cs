using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// The stamp below which a history read may deliver rows: no transaction still in flight can commit
/// a row stamped under it, so a client's cursor never passes a row it has not been sent.
/// </summary>
/// <remarks>
/// A row is stamped before its transaction commits, and a long one (a bulk delete) can commit
/// seconds after rows other transactions stamped later. A read in between returns those later rows
/// and moves the client past the stamps still to commit. The horizon is the start of the oldest
/// other transaction in the database that holds a transaction id, capped at the database clock and
/// truncated to the millisecond a cursor carries. It covers a writer whose every stamp is at or
/// after its transaction start; <see cref="StampAsync"/> takes such a stamp. A tracked save stamps
/// on the application clock just before its first statement, so it is covered only to within that
/// gap and the skew between the two clocks.
/// <para>
/// The horizon reads <c>pg_stat_activity</c>, whose <c>xact_start</c> is visible only for sessions
/// of the reader's own role, so a writer on another role is not waited for. A writer older than
/// <see cref="MaxWriterAge"/> is not waited for either, so a transaction left open cannot stall
/// every history read. While a writer is in flight, history in every tenant lags to its start.
/// </para>
/// <para>
/// Reads merged into one page must all be cut at the same horizon, or one cut lower than another
/// lets the merge pass rows the lower one held back; <see cref="Share"/> gives them one.
/// </para>
/// </remarks>
public static class HistoryHorizon
{
    internal static readonly TimeSpan MaxWriterAge = TimeSpan.FromMinutes(10);

    private static readonly AsyncLocal<Scope?> Current = new();

    // The clock is read before the activity scan, so a writer that claims its id after the scan
    // stamps after the clock this returns.
    private static readonly string HorizonSql = $"""
        SELECT least(c.now, (
            SELECT min(a.xact_start)
            FROM pg_stat_activity a
            WHERE a.datname = current_database()
              AND a.pid <> pg_backend_pid()
              AND a.backend_type = 'client backend'
              AND a.backend_xid IS NOT NULL
              AND a.xact_start > c.now - make_interval(secs => {(int)MaxWriterAge.TotalSeconds})
        )) AS "Value"
        FROM (SELECT clock_timestamp() AS now OFFSET 0) c
        """;

    private const string StampSql = """
        SELECT clock_timestamp() AS "Value"
        FROM (SELECT pg_current_xact_id() AS xid OFFSET 0) x
        WHERE x.xid IS NOT NULL
        """;

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
    public static Task<DateTime?> GetAsync(DbContext context, CancellationToken ct) =>
        Current.Value is { } scope ? scope.GetAsync(context, ct) : ReadAsync(context, ct);

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

    private static async Task<DateTime?> ReadAsync(DbContext context, CancellationToken ct)
    {
        if (!context.Database.IsNpgsql())
            return null;

        var horizon = await context.Database.SqlQueryRaw<DateTime>(HorizonSql).SingleAsync(ct);
        return DateTimeOffset.FromUnixTimeMilliseconds(HistoryPage.ToMilliseconds(horizon)).UtcDateTime;
    }

    /// <summary>The horizon shared by the history reads of one page.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly Scope? _outer;
        private readonly Lock _gate = new();
        private Task<DateTime?>? _horizon;

        internal Scope(Scope? outer) => _outer = outer;

        internal Task<DateTime?> GetAsync(DbContext context, CancellationToken ct)
        {
            if (_outer is not null)
                return _outer.GetAsync(context, ct);

            lock (_gate)
                return _horizon ??= ReadAsync(context, ct);
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
