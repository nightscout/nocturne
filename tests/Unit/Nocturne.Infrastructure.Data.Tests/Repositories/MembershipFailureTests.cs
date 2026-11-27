using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A loop over memberships isolates a failure of the database and a malformed row, and lets a
/// defect reach its caller.
/// </summary>
[Trait("Category", "Unit")]
public class MembershipFailureTests
{
    public static TheoryData<Exception> Isolated => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted", new NpgsqlException("connection refused")),
        new DbUpdateException("save failed", new NpgsqlException("connection refused")),
        new InvalidOperationException("Invalid token type: 'Number'."),
        new ArgumentException("Requested value 'not_a_channel' was not found."),
    };

    public static TheoryData<Exception> Defects => new()
    {
        new NullReferenceException(),
        new TimeoutException(),
        new NotSupportedException(),
        new System.Text.Json.JsonException("defect"),
        new InvalidCastException("defect"),
        new FormatException("defect"),
    };

    [Theory]
    [MemberData(nameof(Isolated))]
    public async Task A_membership_that_fails_is_handed_to_the_log_and_the_rest_still_run(Exception failure)
    {
        var ran = new List<int>();
        var logged = new List<(int Membership, Exception Failure)>();

        var failed = await MembershipReasons.EachMembershipAsync(
            [1, 2, 3],
            membership =>
            {
                ran.Add(membership);
                return membership == 2 ? Task.FromException(failure) : Task.CompletedTask;
            },
            (membership, ex) => logged.Add((membership, ex)));

        failed.Should().Be(1);
        ran.Should().Equal(1, 2, 3);
        logged.Should().ContainSingle().Which.Should().Be((2, failure));
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public async Task A_defect_stops_the_loop_and_reaches_the_caller(Exception defect)
    {
        var ran = new List<int>();

        var act = () => MembershipReasons.EachMembershipAsync(
            [1, 2, 3],
            membership =>
            {
                ran.Add(membership);
                return membership == 2 ? Task.FromException(defect) : Task.CompletedTask;
            },
            (_, _) => { });

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(defect);
        ran.Should().Equal(1, 2);
    }

    [Theory]
    [MemberData(nameof(Isolated))]
    public void A_failure_of_the_database_or_a_malformed_row_is_a_membership_failure(Exception failure)
    {
        MembershipReasons.IsMembershipFailure(failure).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public void A_defect_is_not_a_membership_failure(Exception defect)
    {
        MembershipReasons.IsMembershipFailure(defect).Should().BeFalse();
    }
}
