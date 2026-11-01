using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Classifies save failures by what the database rejected, so a caller can tell a lost race from a
/// rejection retrying cannot fix.
/// </summary>
public static class DbUpdateExceptionExtensions
{
    /// <summary>
    /// True when the save was rejected by a unique index or constraint, which is the one rejection a
    /// read-then-insert can lose to a concurrent writer and win by re-reading. Everything else — a
    /// foreign key, a check, a value too long — is deterministic and a retry only repeats it.
    /// </summary>
    /// <param name="ex">The exception <c>SaveChanges</c> threw.</param>
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is DbException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// True when the save was rejected for the data of a row: SQLSTATE class 22 (a value the column
    /// cannot hold) or class 23 (an integrity constraint). Writing the rows one at a time can then
    /// save the rows that were not refused. A connection, timeout or cancellation failure is not one.
    /// </summary>
    /// <param name="ex">The exception <c>SaveChanges</c> threw.</param>
    public static bool IsDataError(this DbUpdateException ex) =>
        ex.InnerException is DbException { SqlState: ['2', '2' or '3', ..] };
}
