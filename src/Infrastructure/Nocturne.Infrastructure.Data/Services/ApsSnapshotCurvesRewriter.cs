using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Rewrites stored APS snapshots into the shape <c>ApsSnapshotCurves</c> in the API writes and
/// reads: curves the <c>predicted_*</c> columns hold leave the command blobs, Loop's copies of
/// <c>loop.recommended</c>, <c>loop.enacted</c> and <c>loop.predicted.values</c> go, and
/// null-valued keys go.
/// </summary>
/// <remarks>
/// <para>
/// A tenant's snapshots are walked in pages along <c>(sys_updated_at, id)</c>, which
/// <c>ix_aps_snapshots_tenant_history</c> orders, and each page is one statement that writes only
/// the rows whose rewrite differs. A rerun therefore reads and writes nothing, an interrupted run
/// keeps every committed page, and <c>sys_updated_at</c> is left alone so v3 history does not
/// resend documents whose projection is unchanged.
/// </para>
/// <para>
/// The new values are computed in the UPDATE's own SET and WHERE from the target row, so a row an
/// upload changes mid-statement is rewritten from the upload's version rather than overwritten
/// with a rewrite of the version it replaced.
/// </para>
/// </remarks>
public static class ApsSnapshotCurvesRewriter
{
    /// <summary>The curves the <c>predicted_*</c> columns hold, as a command's <c>predBGs</c> carries them.</summary>
    public const string Curves = """
        NULLIF(jsonb_strip_nulls(jsonb_build_object(
            'IOB', predicted_iob_json, 'ZT', predicted_zt_json,
            'COB', predicted_cob_json, 'UAM', predicted_uam_json)), '{}'::jsonb)
        """;

    private static readonly string OrefSuggested = StripCommand("a.suggested_json");
    private static readonly string OrefEnacted = StripCommand("a.enacted_json");

    private const string LoopSuggested =
        "CASE WHEN a.suggested_json = a.loop_json -> 'recommended' THEN NULL ELSE a.suggested_json END";

    private const string LoopEnacted =
        "CASE WHEN a.enacted_json = a.loop_json -> 'enacted' THEN NULL ELSE a.enacted_json END";

    private const string LoopLoop = """
        jsonb_strip_nulls(CASE WHEN a.loop_json #> '{predicted,values}' = a.predicted_default_json
                               THEN a.loop_json #- '{predicted,values}' ELSE a.loop_json END)
        """;

    private static readonly string PageSql = $$"""
        WITH page AS (
            SELECT id, sys_updated_at
            FROM aps_snapshots
            WHERE tenant_id = @tenant AND (sys_updated_at, id) > (@after_at, @after_id)
            ORDER BY sys_updated_at, id
            LIMIT @size),
        oref AS (
            UPDATE aps_snapshots a
            SET suggested_json = {{OrefSuggested}}, enacted_json = {{OrefEnacted}}
            FROM page
            WHERE a.id = page.id
              AND a.loop_json IS NULL AND (a.suggested_json ? 'predBGs' OR a.enacted_json ? 'predBGs')
              AND (a.suggested_json IS DISTINCT FROM {{OrefSuggested}}
                   OR a.enacted_json IS DISTINCT FROM {{OrefEnacted}})
            RETURNING 1),
        loop AS (
            UPDATE aps_snapshots a
            SET suggested_json = {{LoopSuggested}}, enacted_json = {{LoopEnacted}}, loop_json = {{LoopLoop}}
            FROM page
            WHERE a.id = page.id
              AND a.loop_json IS NOT NULL
              AND (a.suggested_json IS NOT NULL OR a.enacted_json IS NOT NULL
                   OR a.loop_json #> '{predicted,values}' IS NOT NULL)
              AND (a.suggested_json IS DISTINCT FROM {{LoopSuggested}}
                   OR a.enacted_json IS DISTINCT FROM {{LoopEnacted}}
                   OR a.loop_json IS DISTINCT FROM {{LoopLoop}})
            RETURNING 1)
        SELECT (SELECT count(*) FROM oref) + (SELECT count(*) FROM loop), last.sys_updated_at, last.id
        FROM (SELECT sys_updated_at, id FROM page ORDER BY sys_updated_at DESC, id DESC LIMIT 1) last
        """;

    /// <summary>
    /// Rewrites every snapshot of the tenant <paramref name="db"/> is pinned to, a page at a time,
    /// and answers how many changed. Each page commits on its own, retried on a transient failure.
    /// </summary>
    /// <param name="db">A context pinned to <paramref name="tenantId"/>.</param>
    /// <param name="tenantId">The tenant whose snapshots to rewrite.</param>
    /// <param name="pageSize">Rows read per page.</param>
    /// <param name="pauseAfterWrite">The pause after a page that wrote, which leaves the database to live traffic.</param>
    /// <param name="pauseAfterRead">The pause after a page that wrote nothing, so a rewritten table's read is throttled too.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<int> RewriteTenantAsync(
        NocturneDbContext db,
        Guid tenantId,
        int pageSize,
        TimeSpan pauseAfterWrite,
        TimeSpan pauseAfterRead,
        CancellationToken ct)
    {
        var afterAt = DateTime.UnixEpoch;
        var afterId = Guid.Empty;
        var changed = 0;

        while (true)
        {
            var page = await db.ExecuteInTransactionAsync(
                attemptCt => RewritePageAsync(db, tenantId, afterAt, afterId, pageSize, attemptCt),
                ct: ct);
            if (page is not { } read)
            {
                return changed;
            }

            (afterAt, afterId) = (read.LastAt, read.LastId);
            changed += read.Changed;
            await Task.Delay(read.Changed > 0 ? pauseAfterWrite : pauseAfterRead, ct);
        }
    }

    private static async Task<(int Changed, DateTime LastAt, Guid LastId)?> RewritePageAsync(
        NocturneDbContext db,
        Guid tenantId,
        DateTime afterAt,
        Guid afterId,
        int pageSize,
        CancellationToken ct)
    {
        // Plain ADO.NET on the context's connection, which carries its tenant: EF's raw SQL would
        // read the JSON paths' braces as format placeholders.
        await using var command = new NpgsqlCommand(PageSql, (NpgsqlConnection)db.Database.GetDbConnection())
        {
            Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction(),
        };
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("after_at", afterAt);
        command.Parameters.AddWithValue("after_id", afterId);
        command.Parameters.AddWithValue("size", pageSize);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ((int)reader.GetInt64(0), reader.GetDateTime(1), reader.GetGuid(2));
    }

    private static string StripCommand(string column) => $$"""
        CASE WHEN jsonb_strip_nulls({{column}} -> 'predBGs') = {{Curves}} THEN jsonb_strip_nulls({{column}} - 'predBGs')
             WHEN {{column}} -> 'predBGs' = 'null'::jsonb AND {{Curves}} IS NOT NULL
                 THEN jsonb_strip_nulls({{column}}) || '{"predBGs": null}'::jsonb
             ELSE jsonb_strip_nulls({{column}}) END
        """;
}
