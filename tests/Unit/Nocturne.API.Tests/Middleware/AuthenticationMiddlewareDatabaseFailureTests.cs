using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// A database that cannot be read says nothing about the credential presented, so it must not reach
/// the client as a 401: a client that sees one may discard a token that is still valid.
/// </summary>
[Trait("Category", "Unit")]
public class AuthenticationMiddlewareDatabaseFailureTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    private sealed class StubHandler(Func<AuthResult> authenticate) : IAuthHandler
    {
        public int Priority => 50;

        public string Name => "Stub";

        public Task<AuthResult> AuthenticateAsync(HttpContext httpContext) => Task.FromResult(authenticate());
    }

    public static TheoryData<Exception> DatabaseFailures => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted"),
        new OperationCanceledException(),
    };

    private static (AuthenticationMiddleware Middleware, DefaultHttpContext Context, Func<bool> NextRan) Build(
        Func<AuthResult> authenticate, Exception? membershipReadFailure = null)
    {
        var contexts = new Mock<INocturneDbContextFactory>();
        if (membershipReadFailure is not null)
        {
            contexts
                .Setup(f => f.CreateForTenantAsync(TenantId, It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromException<NocturneDbContext>(membershipReadFailure));
        }

        var provider = new ServiceCollection()
            .AddSingleton(contexts.Object)
            .AddTransient<ITenantMembershipRepository, TenantMembershipRepository>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["TenantContext"] = new TenantContext(TenantId, "site", "Site", true, false);

        var nextRan = false;
        var middleware = new AuthenticationMiddleware(
            next: _ =>
            {
                nextRan = true;
                return Task.CompletedTask;
            },
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [new StubHandler(authenticate)],
            environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
            publicAccessCacheService: null!,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        return (middleware, context, () => nextRan);
    }

    [Theory]
    [MemberData(nameof(DatabaseFailures))]
    public async Task A_membership_read_that_fails_propagates_instead_of_rejecting_the_session(Exception failure)
    {
        var (middleware, context, nextRan) = Build(
            () => AuthResult.Success(new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.SessionCookie,
                SubjectId = SubjectId,
                TenantId = TenantId,
            }),
            failure);

        var invoke = () => middleware.InvokeAsync(context);

        (await invoke.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        nextRan().Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(DatabaseFailures))]
    public async Task A_grant_lookup_that_fails_propagates_instead_of_rejecting_the_token(Exception failure)
    {
        var (middleware, context, nextRan) = Build(() => throw failure);

        var invoke = () => middleware.InvokeAsync(context);

        (await invoke.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        nextRan().Should().BeFalse();
    }

    [Fact]
    public async Task A_handler_fault_that_is_not_the_database_still_leaves_the_request_unauthenticated()
    {
        var (middleware, context, nextRan) = Build(() => throw new FormatException("bad token"));

        await middleware.InvokeAsync(context);

        nextRan().Should().BeTrue();
        context.GetAuthContext()!.IsAuthenticated.Should().BeFalse();
    }
}
