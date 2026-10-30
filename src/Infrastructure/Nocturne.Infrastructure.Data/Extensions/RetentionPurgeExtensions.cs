using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Batched hard-delete for the retention sweeps that age rows out of a tenant-scoped table:
/// expired audit records and soft-deleted rows past their retention window.
/// </summary>
/// <remarks>
/// <para>
/// The tenant reach the DELETE needs comes from
/// <see cref="INocturneDbContextFactory.CreateForTenantAsync"/> and cannot come from a
/// <c>set_config</c> issued as its own command: EF opens and closes the connection around each
/// command, and the pool's DISCARD ALL clears the session variable on the way back. Every
/// tenant-scoped table is <c>FORCE ROW LEVEL SECURITY</c>, so an unpinned DELETE evaluates
/// <c>tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid</c> against
/// NULL, matches nothing, and reports success having deleted no rows.
/// </para>
/// <para>
/// Pinning happens here rather than at each call site so no sweep can get it wrong. The
/// identifier validation proves only that the interpolated strings are safe to embed, not that
/// the target is tenant-scoped — the delete's tenant bound comes from RLS, with an explicit
/// <c>tenant_id</c> predicate as the backstop for a target that is not tenant-scoped, or is
/// <c>ENABLE</c> without <c>FORCE</c>, where the policy would not bound it. The predicate is
/// free on the audit tables, which index <c>(tenant_id, created_at)</c>, and free on the
/// soft-deletable tables too — they have no <c>(tenant_id, deleted_at)</c> index, so it is
/// evaluated as a filter over rows the partial <c>deleted_at</c> index already selected, which
/// RLS would have filtered anyway.
/// </para>
/// <para>
/// The window is taken as a minimum age rather than an absolute cutoff so a caller cannot ask
/// for a cutoff at or after now — a zero or negative retention window would otherwise delete
/// rows written moments earlier, up to the whole table. The cutoff is derived once, before the
/// first batch, so it cannot creep forward across a long sweep.
/// </para>
/// </remarks>
public static partial class RetentionPurgeExtensions
{
    /// <summary>Rows deleted per statement, bounding WAL growth and transaction duration.</summary>
    public const int DefaultBatchSize = 10_000;

    // \z rather than $: $ also matches before a trailing newline, which a validator guarding a
    // DELETE must not accept.
    [GeneratedRegex(@"\A[a-z_][a-z0-9_]*\z")]
    private static partial Regex SafeIdentifier { get; }

    /// <summary>
    /// Hard-deletes rows of <paramref name="table"/> whose <paramref name="timestampColumn"/> is
    /// older than <paramref name="minAge"/>, within one tenant, in batches.
    /// </summary>
    /// <param name="factory">The context factory.</param>
    /// <param name="tenantId">The tenant whose rows are being aged out.</param>
    /// <param name="table">Table to purge. Must be a bare lowercase SQL identifier.</param>
    /// <param name="timestampColumn">Age column. Must be a bare lowercase SQL identifier.</param>
    /// <param name="minAge">Retention window. Rows strictly older than this are deleted.</param>
    /// <param name="batchSize">Rows per statement. Must be at least 1.</param>
    /// <param name="linkedRecordType">
    /// The <see cref="Core.Models.RecordTypeKeys"/> key the table's rows are linked under in
    /// <c>linked_records</c>, or null for a table deduplication does not cover. When given, each
    /// batch deletes the purged rows' links in the same statement, so a hard delete leaves no link
    /// behind for a sweep to find (<see cref="DeduplicationService.DeleteOrphanedLinksAsync"/>).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total number of rows deleted, links not counted.</returns>
    /// <exception cref="ArgumentException">An identifier is not a bare lowercase identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minAge"/> is not positive, or <paramref name="batchSize"/> is below 1.
    /// </exception>
    public static async Task<int> PurgeOlderThanAsync(
        this INocturneDbContextFactory factory,
        Guid tenantId,
        string table,
        string timestampColumn,
        TimeSpan minAge,
        int batchSize = DefaultBatchSize,
        string? linkedRecordType = null,
        CancellationToken ct = default)
    {
        // Both identifiers are interpolated into SQL, so they are validated rather than trusted:
        // today's callers pass literals and EF model table names, and this keeps that true.
        RequireIdentifier(table, nameof(table));
        RequireIdentifier(timestampColumn, nameof(timestampColumn));

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minAge, TimeSpan.Zero, nameof(minAge));

        // A LIMIT 0 deletes nothing, so the sweep would never make progress.
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var cutoff = DateTime.UtcNow - minAge;

        // Values are bound as parameters; only the validated identifiers and the batch size are
        // interpolated. The ctid sub-select keeps each statement to a bounded slice of the table.
        // Composed into a local, which is also why EF1002/EF1003 do not fire on the call below —
        // the analyzer cannot see the literal, so it is the validation above, not a suppression,
        // that carries the safety argument.
        var sql = $$"""
            DELETE FROM {{table}}
            WHERE tenant_id = {1}
              AND ctid IN (
                SELECT ctid FROM {{table}}
                WHERE tenant_id = {1} AND {{timestampColumn}} < {0}
                LIMIT {{batchSize}})
            """;

        object[] parameters = [cutoff, tenantId];

        // The links go in the same statement as their rows, so neither can outlive the other. The
        // slice is a CTE both deletes read, so they agree on it; the row delete stays the outer
        // statement so the command's affected rows still count rows.
        if (linkedRecordType is not null)
        {
            sql = $$"""
                WITH doomed AS (
                    SELECT ctid, id FROM {{table}}
                    WHERE tenant_id = {1} AND {{timestampColumn}} < {0}
                    LIMIT {{batchSize}}),
                unlinked AS (
                    DELETE FROM linked_records
                    WHERE tenant_id = {1} AND record_type = {2}
                      AND record_id IN (SELECT id FROM doomed))
                DELETE FROM {{table}}
                WHERE tenant_id = {1}
                  AND ctid IN (SELECT ctid FROM doomed)
                """;
            parameters = [cutoff, tenantId, linkedRecordType];
        }

        var totalDeleted = 0;
        int batchDeleted;

        do
        {
            await using var db = await factory.CreateForTenantAsync(tenantId, ct);

            batchDeleted = await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);

            totalDeleted += batchDeleted;
        }
        while (batchDeleted >= batchSize);

        return totalDeleted;
    }

    private static void RequireIdentifier(string value, string paramName)
    {
        if (!SafeIdentifier.IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a bare lowercase SQL identifier.", paramName);
        }
    }
}
