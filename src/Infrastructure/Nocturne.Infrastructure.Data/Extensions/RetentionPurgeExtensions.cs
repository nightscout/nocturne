using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Batched hard-delete for the retention sweeps that age rows out of a tenant-scoped table:
/// expired audit records and soft-deleted rows past their retention window.
/// </summary>
/// <remarks>
/// <para>
/// The tenant reach the DELETE needs comes from
/// <see cref="RlsPinningExtensions.CreateTenantPinnedContextAsync"/> and cannot come from a
/// <c>set_config</c> issued as its own command: EF opens and closes the connection around each
/// command, and <c>TenantConnectionInterceptor</c>'s close resets the session variable. Every
/// tenant-scoped table is <c>FORCE ROW LEVEL SECURITY</c>, so an unpinned DELETE evaluates
/// <c>tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid</c> against
/// NULL, matches nothing, and reports success having deleted no rows.
/// </para>
/// <para>
/// Pinning happens here rather than at each call site so no sweep can get it wrong. The
/// identifier validation proves only that the interpolated strings are safe to embed, not that
/// the target is tenant-scoped — the delete's tenant bound comes from RLS, with an explicit
/// <c>tenant_id</c> predicate as the backstop.
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
    /// before <paramref name="cutoff"/>, within one tenant, in batches.
    /// </summary>
    /// <param name="factory">The context factory.</param>
    /// <param name="tenantId">The tenant whose rows are being aged out.</param>
    /// <param name="table">Table to purge. Must be a bare lowercase SQL identifier.</param>
    /// <param name="timestampColumn">Age column. Must be a bare lowercase SQL identifier.</param>
    /// <param name="cutoff">Rows strictly older than this are deleted.</param>
    /// <param name="batchSize">Rows per statement. Must be at least 1.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total number of rows deleted.</returns>
    /// <exception cref="ArgumentException">An identifier is not a bare lowercase identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="batchSize"/> is below 1, or <paramref name="cutoff"/> is not in the past.
    /// </exception>
    public static async Task<int> PurgeOlderThanAsync(
        this IDbContextFactory<NocturneDbContext> factory,
        Guid tenantId,
        string table,
        string timestampColumn,
        DateTime cutoff,
        int batchSize = DefaultBatchSize,
        CancellationToken ct = default)
    {
        // Both identifiers are interpolated into SQL, so they are validated rather than trusted:
        // today's callers pass literals and EF model table names, and this keeps that true.
        RequireIdentifier(table, nameof(table));
        RequireIdentifier(timestampColumn, nameof(timestampColumn));

        // A LIMIT 0 would delete nothing and never satisfy the loop's exit condition.
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        // A cutoff at or after now deletes rows written moments ago, up to the entire table. A
        // retention window is always historical, so a non-past cutoff is a caller bug, and
        // refusing it keeps a bad retention setting from erasing the record it is meant to age.
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cutoff, DateTime.UtcNow, nameof(cutoff));

        var totalDeleted = 0;
        int batchDeleted;

        do
        {
            await using var db = await factory.CreateTenantPinnedContextAsync(tenantId, ct);

            // tenant_id is constrained in SQL as well as by RLS. RLS is the guarantee; this is the
            // backstop for a table that is not tenant-scoped, or is ENABLE without FORCE, where
            // the policy would not bound the delete. The plan enters on (tenant_id, <column>)
            // either way, so it costs nothing.
            //
            // ctid sub-select keeps each statement to a bounded slice of the table.
#pragma warning disable EF1002
            batchDeleted = await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM {table} WHERE tenant_id = {{1}} AND ctid IN "
                + $"(SELECT ctid FROM {table} WHERE tenant_id = {{1}} AND {timestampColumn} < {{0}} "
                + $"LIMIT {batchSize})",
                [cutoff, tenantId], ct);
#pragma warning restore EF1002

            totalDeleted += batchDeleted;
        }
        while (batchDeleted > 0);

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
