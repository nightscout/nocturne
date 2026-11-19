using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// A context's SQLite schema, created once per test process and copied page for page into each new
/// in-memory database. <c>EnsureCreated</c> diffs the whole model and runs its DDL, around half a
/// second every time, which made schema creation most of the unit suites' run time; a backup copy of
/// the finished database is a few milliseconds.
/// </summary>
/// <param name="createContext">A context over the given connection whose <c>EnsureCreated</c>
/// builds the schema.</param>
public sealed class SqliteSchemaTemplate(Func<SqliteConnection, DbContext> createContext)
{
    private readonly Lazy<SqliteConnection> _template = new(() =>
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = createContext(connection);
        context.Database.EnsureCreated();
        return connection;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // One connection is not safe for concurrent use, and test classes run in parallel.
    private readonly Lock _copyLock = new();

    public void CopyInto(SqliteConnection destination)
    {
        lock (_copyLock)
        {
            _template.Value.BackupDatabase(destination);
        }
    }
}
