using Npgsql;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Turns "the contender is still waiting for the lock" into an observed fact rather than a fixed
/// sleep followed by <c>IsCompleted == false</c>, which passes whether or not the contender had
/// even reached the lock.
/// </summary>
public static class PostgresLockWaits
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Returns once a session in the connection's database is waiting on a lock, and throws if
    /// <paramref name="contender"/> finishes first. The database must hold no other test's
    /// sessions at the time: any lock waiter in it is taken to be the contender.
    /// </summary>
    public static Task UntilBlockedAsync(string connectionString, Task contender, TimeSpan? timeout = null) =>
        UntilAsync(
            connectionString,
            "the contender to wait on the held lock",
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock')
            """,
            null,
            result => (bool)result!,
            "The contender finished while the lock was held.",
            [contender],
            timeout);

    /// <summary>
    /// Returns once a session in the connection's database is waiting on a lock held by backend
    /// <paramref name="blockerPid"/> (<c>NpgsqlConnection.ProcessID</c>), and throws if
    /// <paramref name="contender"/> finishes first. Waiters on other backends' locks, such as a hosted
    /// service's, do not count.
    /// </summary>
    public static Task UntilBlockedByAsync(
        string connectionString, int blockerPid, Task contender, TimeSpan? timeout = null) =>
        UntilAsync(
            connectionString,
            $"the contender to wait on a lock held by backend {blockerPid}",
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND @pid = ANY(pg_blocking_pids(pid)))
            """,
            blockerPid,
            result => (bool)result!,
            "The contender finished while the lock was held.",
            [contender],
            timeout);

    /// <summary>
    /// Returns once backend <paramref name="waiterPid"/> (<c>NpgsqlConnection.ProcessID</c>) is
    /// waiting on a lock, and throws if <paramref name="contender"/> finishes first.
    /// </summary>
    public static Task UntilSessionBlockedAsync(
        string connectionString, int waiterPid, Task contender, TimeSpan? timeout = null) =>
        UntilAsync(
            connectionString,
            $"backend {waiterPid} to wait on a lock",
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock')
            """,
            waiterPid,
            result => (bool)result!,
            "The contender finished while the lock was held.",
            [contender],
            timeout);

    /// <summary>
    /// Returns once <paramref name="sessions"/> sessions in the connection's database are waiting on a
    /// lock, and throws if any of <paramref name="contenders"/> finishes first. The database must hold
    /// no other test's sessions at the time, as for <see cref="UntilBlockedAsync"/>.
    /// </summary>
    public static Task UntilWaitingAsync(string connectionString, int sessions, params Task[] contenders) =>
        UntilAsync(
            connectionString,
            $"{sessions} sessions to wait on the held lock",
            """
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock'
            """,
            null,
            result => Convert.ToInt32(result) >= sessions,
            "A contender finished while the lock was held.",
            contenders,
            null);

    private static async Task UntilAsync(
        string connectionString,
        string description,
        string query,
        int? pid,
        Func<object?, bool> holds,
        string finishedFirst,
        Task[] contenders,
        TimeSpan? timeout)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        await Eventually.UntilAsync(
            description,
            async () =>
            {
                if (contenders.FirstOrDefault(c => c.IsCompleted) is { } finished)
                    throw new InvalidOperationException(finishedFirst, finished.Exception);

                await using var command = observer.CreateCommand();
                command.CommandText = query;
                if (pid is { } value)
                    command.Parameters.Add(new NpgsqlParameter("pid", value));
                return holds(await command.ExecuteScalarAsync());
            },
            timeout,
            Step);
    }
}
