using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The enrolment exists whatever happens when the session ends, so a store that cannot void the
/// member's recovery codes is logged; a defect is not a store failure and reaches the caller.
/// </summary>
[Trait("Category", "Unit")]
public class RecoverySessionEndTests
{
    private static readonly Guid SubjectId = Guid.NewGuid();

    public static TheoryData<Exception> StoreFailures => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted", new NpgsqlException("connection refused")),
        new DbUpdateException("save failed", new NpgsqlException("connection refused")),
    };

    public static TheoryData<Exception> Defects => new()
    {
        new NullReferenceException(),
        new InvalidOperationException("Database role 'postgres' bypasses Row Level Security"),
        new TimeoutException(),
    };

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task A_store_failure_while_voiding_is_logged_and_the_cookie_is_still_ended(Exception failure)
    {
        var (session, http) = Build(failure);

        await session.EndAsync(http, SubjectId, CancellationToken.None);

        http.Response.Headers.SetCookie.ToString().Should().Contain(RecoverySession.CookieName);
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public async Task A_defect_while_voiding_reaches_the_caller(Exception defect)
    {
        var (session, http) = Build(defect);

        var act = () => session.EndAsync(http, SubjectId, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(defect);
    }

    private static (RecoverySession Session, HttpContext Http) Build(Exception voidFailure)
    {
        var loginCodes = new Mock<ILoginCodeService>();
        loginCodes
            .Setup(l => l.VoidRecoveryCodesAsync(It.IsAny<NocturneDbContext>(), SubjectId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(voidFailure);
        var contexts = new Mock<INocturneDbContextFactory>();
        contexts
            .Setup(c => c.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(TestDbContextFactory.CreateInMemoryContext()));

        var session = new RecoverySession(
            contexts.Object, loginCodes.Object, new Mock<ITenantMemberService>().Object,
            Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance);
        return (session, new DefaultHttpContext());
    }
}
