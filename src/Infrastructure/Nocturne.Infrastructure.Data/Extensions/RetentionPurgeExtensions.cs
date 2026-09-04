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
/// Pinning happens here rather than at each call site so a sweep cannot acquire its context
/// any other way.
/// </para>
/// </remarks>
public static partial class RetentionPurgeExtensions
{
    /// <summary>Rows deleted per statement, bounding WAL growth and transaction duration.</summary>
    public const int DefaultBatchSize = 10_000;

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
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
    /// <param name="batchSize">Rows per statement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total number of rows deleted.</returns>
    /// <exception cref="ArgumentException">An identifier is not a bare lowercase identifier.</exception>
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

        var totalDeleted = 0;
        int batchDeleted;

        do
        {
            await using var db = await factory.CreateTenantPinnedContextAsync(tenantId, ct);

            // ctid sub-select keeps each statement to a bounded slice of the table.
#pragma warning disable EF1002
            batchDeleted = await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM {table} WHERE ctid IN "
                + $"(SELECT ctid FROM {table} WHERE {timestampColumn} < {{0}} LIMIT {batchSize})",
                [cutoff], ct);
#pragma warning restore EF1002

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
