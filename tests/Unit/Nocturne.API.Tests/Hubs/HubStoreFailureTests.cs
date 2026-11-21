using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Hubs;
using Npgsql;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// A hub answers a failure of its database with its failed response, and lets a defect reach the
/// hub pipeline.
/// </summary>
[Trait("Category", "Unit")]
public class HubStoreFailureTests
{
    /// <summary>The failures the database raises through Npgsql and EF.</summary>
    public static TheoryData<Exception> StoreFailures => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted", new NpgsqlException("connection refused")),
        new DbUpdateException("save failed", new NpgsqlException("connection refused")),
    };

    public static TheoryData<Exception> Defects => new()
    {
        new InvalidOperationException("a second operation was started on this context"),
        new InvalidCastException("defect"),
        new NullReferenceException(),
        new OperationCanceledException(),
    };

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public void A_failure_of_the_database_is_a_store_failure(Exception failure)
    {
        HubStoreFailure.Is(failure).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public void A_defect_is_not_a_store_failure(Exception failure)
    {
        HubStoreFailure.Is(failure).Should().BeFalse();
    }
}
