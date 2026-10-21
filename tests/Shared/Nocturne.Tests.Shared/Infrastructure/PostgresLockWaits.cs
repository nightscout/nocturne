using Npgsql;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Turns "the contender is still waiting for the lock" into an observed fact rather than a fixed
/// sleep followed by <c>IsCompleted == false</c>, which passes whether or not the contender had
/// even reached the lock.
/// </summary>
public static class PostgresLockWaits
{
    /// <summary>
    /// Returns once a session in the connection's database is waiting on a lock, and throws if
    /// <paramref name="contender"/> finishes first. The database must hold no other test's
    /// sessions at the time: any lock waiter in it is taken to be the contender.
    /// </summary>
    public static async Task UntilBlockedAsync(string connectionString, Task contender, TimeSpan? timeout = null)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (contender.IsCompleted)
                throw new InvalidOperationException("The contender finished while the lock was held.", contender.Exception);

            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock')
                """;
            if ((bool)(await command.ExecuteScalarAsync())!)
                return;

            await Task.Delay(20);
        }

        throw new TimeoutException("The contender never waited on the held lock.");
    }
}
