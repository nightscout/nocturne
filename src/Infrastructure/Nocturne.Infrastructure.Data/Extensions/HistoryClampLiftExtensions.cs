using Microsoft.EntityFrameworkCore;
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
/// wrote. The one exception is a referential clean-up that must reach hidden rows and returns only a
/// count (<see cref="StateSpanSupersessionExtensions.ClearSupersededByAsync"/>). Its callers are
/// pinned by <c>HistoryClampLiftAllowlistTests</c>.
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
