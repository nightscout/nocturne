using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Has PostgreSQL plan each statement left in the caller's transaction for the values it is given.
/// A transaction that filters by an id list as long as the data it acts on (a source's readings, a
/// chunk of them, the duplicate groups they lead) calls <see cref="PlanForEachArrayAsync"/> once,
/// before its first such statement, with the length of its longest list.
/// </summary>
/// <remarks>
/// <para>
/// The runtime pool auto-prepares (<see cref="Configuration.PostgreSqlConfiguration.MaxAutoPrepare"/>):
/// a statement run five times on a connection is prepared, and from the sixth prepared run PostgreSQL may
/// keep one generic plan for every value. A generic plan sees an array as a parameter. It cannot hash it
/// for <c>= ANY</c> (PostgreSQL hashes only an array it was given as a value), so a filter searches the
/// whole array for every row it tests, and it estimates an <c>unnest</c> of it at ten rows, so a join to
/// it can rescan the array for every row. Where the planner has no statistics for the tables, which
/// gives a scan of the tenant as its cheapest plan, both shapes run tens to hundreds of times longer than when
/// planned for their array, and a lock that joins its array can pass the statement timeout.
/// </para>
/// <para>
/// The setting is <c>SET LOCAL</c>, so it ends with the transaction and the connection goes back to
/// the pool as it came. Statements outside such a transaction keep their generic plans, whose
/// planning auto-prepare is there to save.
/// </para>
/// </remarks>
internal static class ArrayFilterPlans
{
    /// <summary>
    /// The shortest list worth a plan of its own. PostgreSQL hashes an <c>= ANY</c> array from nine
    /// elements; a shorter one is searched end to end in any plan, and an <c>unnest</c> of it is
    /// near the ten rows a generic plan estimates.
    /// </summary>
    internal const int HashedArrayLength = 9;

    /// <summary>
    /// Plans the rest of the transaction for its values when <paramref name="longestList"/> is at
    /// least <see cref="HashedArrayLength"/>. A provider other than PostgreSQL prepares nothing, so
    /// there it does nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on a PostgreSQL context.</exception>
    public static Task PlanForEachArrayAsync(this NocturneDbContext ctx, int longestList, CancellationToken ct)
    {
        if (longestList < HashedArrayLength || !ctx.Database.IsNpgsql())
            return Task.CompletedTask;
        if (ctx.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A plan setting for a transaction needs one open; none is.");

        return ctx.Database.ExecuteSqlRawAsync("SET LOCAL plan_cache_mode = force_custom_plan", ct);
    }
}
