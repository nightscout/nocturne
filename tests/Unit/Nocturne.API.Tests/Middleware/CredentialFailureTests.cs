using FluentAssertions;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Middleware;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

[Trait("Category", "Unit")]
public class CredentialFailureTests
{
    public static TheoryData<Exception> InfrastructureFailures => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted"),
        new OperationCanceledException(),
        new TaskCanceledException("request timed out"),
        new HttpRequestException("name resolution failed"),
        new IOException("connection reset"),
        new InvalidOperationException("could not retrieve configuration", new HttpRequestException("unreachable")),
        new InvalidOperationException("could not save", new InvalidOperationException("wrapped", new NpgsqlException("down"))),
    };

    public static TheoryData<Exception> CredentialFailures => new()
    {
        new FormatException("bad token"),
        new InvalidOperationException("OIDC token missing subject identifier"),
        new UnauthorizedAccessException(),
    };

    [Theory]
    [MemberData(nameof(InfrastructureFailures))]
    public void A_failure_of_the_infrastructure_is_not_a_verdict_on_the_credential(Exception failure) =>
        CredentialFailure.Is(failure).Should().BeFalse();

    [Theory]
    [MemberData(nameof(CredentialFailures))]
    public void A_failure_of_the_credential_is_a_verdict_on_it(Exception failure) =>
        CredentialFailure.Is(failure).Should().BeTrue();
}
