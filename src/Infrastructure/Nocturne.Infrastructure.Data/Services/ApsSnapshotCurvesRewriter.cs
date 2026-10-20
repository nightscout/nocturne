using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Rewrites stored APS snapshots into the shape <c>ApsSnapshotCurves</c> in the API writes and
/// reads: curves the <c>predicted_*</c> columns hold leave the command blobs, Loop's copies of
/// <c>loop.recommended</c>, <c>loop.enacted</c> and <c>loop.predicted.values</c> go, and
/// null-valued keys go.
/// </summary>
/// <remarks>
/// Run in batches by <c>ApsSnapshotCurvesBackfillService</c> rather than by the
/// <c>DeduplicateApsSnapshotCurves</c> migration, which once held production's API down past the
/// one-hour migration timeout without finishing: the API reads both shapes, so the rewrite only
/// reclaims storage and need not gate startup. A row is written only when its rewrite differs, so
/// a rerun writes nothing, and <c>sys_updated_at</c> is left alone so v3 history does not resend
/// unchanged documents.
/// </remarks>
public static class ApsSnapshotCurvesRewriter
{
    /// <summary>The curves the <c>predicted_*</c> columns hold, as a command's <c>predBGs</c> carries them.</summary>
    public const string Curves = """
        NULLIF(jsonb_strip_nulls(jsonb_build_object(
            'IOB', predicted_iob_json, 'ZT', predicted_zt_json,
            'COB', predicted_cob_json, 'UAM', predicted_uam_json)), '{}'::jsonb)
        """;

    private const string OrefNeedsRewrite =
        "loop_json IS NULL AND (suggested_json ? 'predBGs' OR enacted_json ? 'predBGs')";

    private const string LoopNeedsRewrite = """
        loop_json IS NOT NULL
        AND (suggested_json IS NOT NULL OR enacted_json IS NOT NULL
             OR loop_json #> '{predicted,values}' IS NOT NULL)
        """;

    private static readonly string CandidatesSql = $"""
        SELECT id FROM aps_snapshots
        WHERE tenant_id = @tenant AND (({OrefNeedsRewrite}) OR ({LoopNeedsRewrite}))
        ORDER BY id
        """;

    private static readonly string RewriteOrefSql = $$"""
        WITH c AS (
            SELECT id, {{Curves}} AS curves
            FROM aps_snapshots
            WHERE id = ANY(@ids) AND {{OrefNeedsRewrite}}),
        n AS (
            SELECT a.id, {{StripCommand("a.suggested_json")}} AS suggested, {{StripCommand("a.enacted_json")}} AS enacted
            FROM aps_snapshots a JOIN c ON c.id = a.id)
        UPDATE aps_snapshots a
        SET suggested_json = n.suggested, enacted_json = n.enacted
        FROM n
        WHERE n.id = a.id
          AND (a.suggested_json IS DISTINCT FROM n.suggested OR a.enacted_json IS DISTINCT FROM n.enacted)
        """;

    private static readonly string RewriteLoopSql = $$"""
        WITH n AS (
            SELECT id,
                   CASE WHEN suggested_json = loop_json -> 'recommended' THEN NULL ELSE suggested_json END AS suggested,
                   CASE WHEN enacted_json = loop_json -> 'enacted' THEN NULL ELSE enacted_json END AS enacted,
                   jsonb_strip_nulls(CASE WHEN loop_json #> '{predicted,values}' = predicted_default_json
                                          THEN loop_json #- '{predicted,values}' ELSE loop_json END) AS loop
            FROM aps_snapshots
            WHERE id = ANY(@ids) AND {{LoopNeedsRewrite}})
        UPDATE aps_snapshots a
        SET suggested_json = n.suggested, enacted_json = n.enacted, loop_json = n.loop
        FROM n
        WHERE n.id = a.id
          AND (a.suggested_json IS DISTINCT FROM n.suggested OR a.enacted_json IS DISTINCT FROM n.enacted
               OR a.loop_json IS DISTINCT FROM n.loop)
        """;

    /// <summary>
    /// The ids of the tenant's snapshots stored in a shape the rewrite may change, in id order.
    /// </summary>
    /// <param name="db">A context pinned to <paramref name="tenantId"/>.</param>
    /// <param name="tenantId">The tenant whose snapshots to list.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<List<Guid>> ListCandidatesAsync(NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command(db, CandidatesSql);
            command.Parameters.AddWithValue("tenant", tenantId);
            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetGuid(0));
            }

            return ids;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Rewrites the given snapshots, in one transaction, and answers how many changed.</summary>
    /// <param name="db">A context pinned to the snapshots' tenant.</param>
    /// <param name="ids">Snapshot ids, from <see cref="ListCandidatesAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<int> RewriteAsync(NocturneDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = 0;
        foreach (var sql in new[] { RewriteOrefSql, RewriteLoopSql })
        {
            await using var command = Command(db, sql);
            command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.ToArray() });
            changed += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return changed;
    }

    /// <summary>
    /// A command on the context's own connection, which carries its tenant. Plain ADO.NET rather
    /// than EF's raw SQL, which would read the JSON paths' braces as format placeholders.
    /// </summary>
    private static NpgsqlCommand Command(NocturneDbContext db, string sql) =>
        new(sql, (NpgsqlConnection)db.Database.GetDbConnection());

    private static string StripCommand(string column) => $$"""
        CASE WHEN jsonb_strip_nulls({{column}} -> 'predBGs') = c.curves THEN jsonb_strip_nulls({{column}} - 'predBGs')
             WHEN {{column}} -> 'predBGs' = 'null'::jsonb AND c.curves IS NOT NULL
                 THEN jsonb_strip_nulls({{column}}) || '{"predBGs": null}'::jsonb
             ELSE jsonb_strip_nulls({{column}}) END
        """;
}
