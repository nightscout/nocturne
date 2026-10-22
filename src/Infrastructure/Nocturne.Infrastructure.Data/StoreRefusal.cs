using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Tells a write PostgreSQL refuses for a value the client sent, which fails the same way however
/// often it is retried, from every other failure.
/// </summary>
/// <remarks>
/// Refused: a string too long (22001), a number out of range (22003), a date or time out of range
/// or malformed (22007, 22008), and text the database encoding cannot hold (22021, 22P05). Every
/// other state, a missing required value (23502) or a malformed cast (22P02) among them, means a
/// Nocturne bug or a store fault, and must not be answered as the client's data. The reason is a
/// fixed sentence: PostgreSQL's own message can name tables and columns.
/// </remarks>
public static class StoreRefusal
{
    /// <returns>A reason fit to show the client for a refused write, otherwise <c>null</c>.</returns>
    public static string? ReasonFor(DbUpdateException ex) =>
        (ex.InnerException as PostgresException)?.SqlState switch
        {
            "22001" => "A text value is longer than Nocturne can store",
            "22003" => "A number is outside the range Nocturne can store",
            "22007" or "22008" => "A date or time is invalid or outside the range Nocturne can store",
            "22021" or "22P05" => "Text contains characters Nocturne cannot store",
            _ => null,
        };
}
