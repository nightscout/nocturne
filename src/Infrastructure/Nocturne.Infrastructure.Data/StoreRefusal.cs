using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Tells a write PostgreSQL refuses for the data it carries, which fails the same way however often
/// it is retried, from one that failed for the state of the store at the time.
/// </summary>
/// <remarks>
/// Refused: SQLSTATE class 22 (a value the column cannot hold: too long, out of range, malformed),
/// 23502 (a required value missing) and 23514 (a check constraint). Not refused: a unique or
/// foreign-key violation, which a concurrent write can cause and a retry can clear, and anything
/// that is not a PostgreSQL error at all.
/// </remarks>
public static class StoreRefusal
{
    /// <returns>PostgreSQL's message for a refused write, otherwise <c>null</c>.</returns>
    public static string? ReasonFor(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: var state } postgres
        && (state.StartsWith("22", StringComparison.Ordinal) || state is "23502" or "23514")
            ? postgres.MessageText
            : null;
}
