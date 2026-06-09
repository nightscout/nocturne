using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// Covers the cold-start connection retry added to the migration bootstrap:
/// on a fresh `docker compose up`, the database container may not be accepting
/// connections yet when the API starts. Transient connection failures must be
/// retried; server-side errors (auth/role/db) must propagate immediately so the
/// existing diagnostic handlers fire.
/// </summary>
public class MigrationConnectionRetryTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public async Task Succeeds_on_first_attempt_when_probe_connects()
    {
        var attempts = 0;
        var made = await DatabaseInitializationExtensions.WaitForConnectableAsync(
            probe: _ => { attempts++; return Task.CompletedTask; },
            isTransient: _ => true,
            maxAttempts: 5,
            retryDelay: TimeSpan.Zero,
            logger: Logger);

        made.Should().Be(1);
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var attempts = 0;
        var made = await DatabaseInitializationExtensions.WaitForConnectableAsync(
            probe: _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new NpgsqlException("connection refused");
                return Task.CompletedTask;
            },
            isTransient: DatabaseInitializationExtensions.IsTransientConnectionFailure,
            maxAttempts: 10,
            retryDelay: TimeSpan.Zero,
            logger: Logger);

        made.Should().Be(3);
    }

    [Fact]
    public async Task Throws_last_exception_after_exhausting_attempts()
    {
        var attempts = 0;
        var act = async () => await DatabaseInitializationExtensions.WaitForConnectableAsync(
            probe: _ => { attempts++; throw new NpgsqlException("still refused"); },
            isTransient: _ => true,
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            logger: Logger);

        await act.Should().ThrowAsync<NpgsqlException>().WithMessage("*still refused*");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Does_not_retry_non_transient_failures()
    {
        var attempts = 0;
        var act = async () => await DatabaseInitializationExtensions.WaitForConnectableAsync(
            probe: _ => { attempts++; throw new InvalidOperationException("permanent"); },
            isTransient: _ => false,
            maxAttempts: 5,
            retryDelay: TimeSpan.Zero,
            logger: Logger);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(TransientCases))]
    public void Classifies_transient_connection_failures(Exception ex, bool expected)
    {
        DatabaseInitializationExtensions.IsTransientConnectionFailure(ex).Should().Be(expected);
    }

    public static IEnumerable<object[]> TransientCases()
    {
        // Connection-level failures the DB-not-ready race produces — retry these.
        yield return [new NpgsqlException("connection refused"), true];
        yield return [new SocketException((int)SocketError.ConnectionRefused), true];
        yield return [new TimeoutException("connect timeout"), true];
        // Server reachable but rejected — do NOT retry; let the diagnostic handlers fire.
        yield return [new PostgresException("role missing", "FATAL", "FATAL", "28P01"), false];
        yield return [new InvalidOperationException("config error"), false];
    }
}
