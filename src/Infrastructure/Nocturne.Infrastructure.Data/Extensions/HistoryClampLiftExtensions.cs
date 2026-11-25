using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Runs a write with the member 24-hour history clamp (<c>app.history_clamped</c>) lifted. The
/// clamp limits what a member reads, not what they may record, but PostgreSQL checks the rows an
/// <c>INSERT … RETURNING</c> or an <c>UPDATE … WHERE</c> writes against the table's SELECT policies,
/// the restrictive <c>share_category_read</c> among them. Without the lift a clamped member recording
/// last month's lab result, or completing a tracker two days back, is refused with 42501.
/// </summary>
/// <remarks>
/// The lift is <c>set_config</c> local to the write's transaction, opened when none is, and is put
/// back once the write returns, so a statement the transaction runs afterwards is clamped again. A
/// lifted statement filters and returns rows as an unclamped reader would, so a write lifted here
/// inserts, or targets rows the clamp already shows its caller, and reads back only the rows it
/// wrote. The exceptions are writes that must reach hidden rows and return only a count: the supersession
/// a removed or arrived span leaves (<see cref="StateSpanSupersessionExtensions"/>), which sets the end and
/// pointer of the spans it ends from a lifted read of their successors and answers them only to a restore
/// and an upload, which take them onto the rows they give back, the duplicate flag
/// (<see cref="DuplicateGroupPrimaries.SetPrimariesAsync"/>) and the stream flag
/// (<see cref="GlucoseStreamFlags"/>), which reads the whole of each bucket it decides, the stamp of a
/// promoted copy (<see cref="DuplicateGroupPrimaries.TouchAsync"/>), the stamp of what write-back
/// is sending and sent (<see cref="Repositories.V4.DecomposedTreatmentKeys.RecordSendingAsync"/>, <see cref="Repositories.V4.DecomposedTreatmentKeys.RecordSentAsync"/>), and the erasure of a deleted
/// account's notices in every tenant (<see cref="Repositories.SubjectRepository"/>), which returns the notices
/// it erased only to broadcast each to the person who held it. A lifted statement may also be a read
/// whose rows feed only an internal decision and never reach the caller: the repick's reads
/// (<see cref="DuplicateGroupPrimaries.LiveIdsAsync"/>, <see cref="DuplicateGroupPrimaries.LiveSpanEndsAsync"/>)
/// and matching's (<see cref="LinkedRecordTypes.LoadRecordInfoAsync"/>, <see cref="LinkedRecordTypes.UnlinkedAsync"/>),
/// the restore's check of the unique keys a restored row would take
/// (<see cref="SoftDeleteRestoreExtensions"/>), which the index enforces over the whole tenant, and
/// the first arrival of each glucose stream, which replay classifies readings by
/// (<see cref="Repositories.V4.SensorGlucoseRepository.GetStreamFirstArrivalsAsync"/>), and whether
/// write-back sent a record (<see cref="Repositories.V4.DecomposedTreatmentKeys.GetSentUpstreamAsync"/>). One lifted read
/// returns a row the clamp hides: a retried clear of a do-not-disturb window the caller names by id, cleared before
/// the clamp's window, answers with that window as the first clear did (<see cref="Repositories.DndWindowRepository"/>).
/// <para>
/// A keyed upsert (<see cref="Repositories.SystemEventRepository"/>, <see cref="Repositories.StateSpanRepository"/>,
/// <see cref="Repositories.SleepSessionRepository"/>, the health series, and a V4 record's sync-key and legacy-id
/// matches in <see cref="Repositories.V4.SyncUpsertRepositoryBase{TModel,TEntity}"/> and
/// <see cref="Repositories.V4.V4RepositoryBase{TModel,TEntity}"/>) lifts the read that matches a record by
/// the key a unique index holds over the whole tenant, and the save that overwrites the match. Without it a
/// clamped writer re-sending a record stored before the window finds no row, inserts, and fails the index with
/// 23505. The match is overwritten with the writer's own record, and the answer is built from what the writer
/// sent and the id of the row it reached, which is the write's own result: a field only the stored row holds
/// (a supersession, a legacy id, a correlation id) is not answered or broadcast for a row the clamp hides. A V4
/// row the clamp hides keeps its correlation id (<see cref="Repositories.V4.KeptCorrelationIds"/>). A V4 insert's
/// check of the legacy ids or correlation ids stored rows hold is lifted for the same reason, and so are the
/// reads that agree with a lifted match: a sync key's recreation check and the stored times a sync-key upsert
/// moves, which only key cache clears.
/// </para>
/// <para>
/// A user's delete or restore of a record the clamp shows its caller reaches every copy in the
/// record's duplicate group (<see cref="DuplicateDelete.EveryCopy"/>), so the copies the clamp hides
/// are read and written by id: tracked by <see cref="DuplicateGroupPrimaries.HiddenRowsAsync{TEntity}"/>
/// and <see cref="DuplicateGroupPrimaries.SaveHiddenAsync{TEntity}"/>, and by statement in the bulk
/// delete (<see cref="AuditedBulkDeleteExtensions"/>). The ids come from the links of the record the
/// caller named, which it could read. What either gives back or broadcasts is only what the caller's
/// clamped context reads. Each hidden copy written gets the audit row a shown one gets, in
/// <c>mutation_audit_log</c>, which is read under its own permission and not clamped.
/// </para>
/// Its callers are pinned by <c>HistoryClampLiftAllowlistTests</c>.
/// <para>
/// The transaction it opens is retried with no <c>verifySucceeded</c>: a commit that reported failure
/// but landed is replayed, and its client-generated keys then fail with 23505 rather than writing
/// twice, as an unlifted save retried by the execution strategy does. A check of the landed rows
/// would run clamped and miss the backdated rows it looks for.
/// </para>
/// </remarks>
internal static class HistoryClampLiftExtensions
{
    private const string HistoryClampedSetting = "app.history_clamped";

    public static async Task<T> WriteWithHistoryClampLiftedAsync<T>(
        this NocturneDbContext ctx, Func<CancellationToken, Task<T>> write, CancellationToken ct)
    {
        if (!ctx.HistoryClamped || !ctx.Database.IsNpgsql())
            return await write(ct);

        if (ctx.Database.CurrentTransaction is null)
        {
            return await ctx.ExecuteInTransactionAsync(
                async token =>
                {
                    await SetHistoryClampedAsync(ctx, false, token);
                    return await write(token);
                },
                ct: ct);
        }

        await SetHistoryClampedAsync(ctx, false, ct);
        try
        {
            return await write(ct);
        }
        finally
        {
            await RestoreAsync(ctx);
        }
    }

    /// <remarks>
    /// Runs after a failed write too: EF rolls a failed save back to its savepoint, and the
    /// transaction then goes on reading.
    /// </remarks>
    private static async Task RestoreAsync(NocturneDbContext ctx)
    {
        try
        {
            await SetHistoryClampedAsync(ctx, true, CancellationToken.None);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
        {
            // The aborted transaction can only roll back, which discards the local setting.
        }
    }

    private static Task SetHistoryClampedAsync(NocturneDbContext ctx, bool clamped, CancellationToken ct) =>
        ctx.Database.ExecuteSqlRawAsync(
            "SELECT set_config({0}, {1}, true)", [HistoryClampedSetting, clamped ? "true" : "false"], ct);
}
