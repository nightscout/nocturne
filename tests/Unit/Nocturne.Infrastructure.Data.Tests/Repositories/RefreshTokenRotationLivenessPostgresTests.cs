using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="EfFirstPartyTokenRepository.TryRotateAsync"/> on PostgreSQL as the runtime role: the
/// session a rotation is under way in reads live from another connection at every point of the
/// rotation, because the old token is revoked and its successor stored in one transaction.
/// </summary>
[Trait("Category", "Integration")]
public class RefreshTokenRotationLivenessPostgresTests(RefreshTokenRotationLivenessPostgresTests.Database database)
    : IClassFixture<RefreshTokenRotationLivenessPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync() =>
            AppConnectionString = (await SharedPostgres.CreateMigratedDatabaseAsync("refresh_token_rotation_liveness")).AppConnectionString;

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class Factory(string connectionString, params IInterceptor[] interceptors) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(TestContexts.Context(connectionString, null, interceptors));
    }

    /// <summary>
    /// Holds the transaction open once the statement that revokes a refresh token has run, until
    /// the test releases it.
    /// </summary>
    private sealed class AfterTheRevoke : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("refresh_tokens", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(Eventually.DefaultPatience, cancellationToken);
            }

            return result;
        }
    }

    private EfFirstPartyTokenRepository Tokens(params IInterceptor[] interceptors) =>
        new(new Factory(database.AppConnectionString, interceptors));

    private static RefreshTokenRecord Token(Guid subjectId, string sessionId) => new(
        Id: Guid.CreateVersion7(),
        TokenHash: Guid.NewGuid().ToString("N"),
        SubjectId: subjectId,
        OidcSessionId: sessionId,
        DeviceDescription: null,
        IpAddress: null,
        UserAgent: null,
        IssuedAt: DateTime.UtcNow,
        ExpiresAt: DateTime.UtcNow.AddDays(7),
        RevokedAt: null,
        RevokedReason: null,
        ReplacedByTokenId: null,
        LastUsedAt: null);

    [Fact]
    public async Task A_session_reads_live_from_another_connection_between_the_revoke_and_the_successor()
    {
        var subjectId = await new SubjectRepository(new Factory(database.AppConnectionString), AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"rotating-{Guid.NewGuid():N}"), CancellationToken.None);
        var sessionId = Guid.CreateVersion7().ToString();
        var presented = Token(subjectId, sessionId);
        await Tokens().CreateAsync(presented);
        var gate = new AfterTheRevoke();

        var rotation = Tokens(gate).TryRotateAsync(presented.Id, Token(subjectId, sessionId));
        await gate.Reached.Task.WaitAsync(Eventually.DefaultPatience);
        var liveMidway = await Tokens().HasLiveSessionAsync(subjectId, sessionId, DateTime.UtcNow);
        gate.Release.SetResult();

        (await rotation).Should().BeTrue();
        liveMidway.Should().BeTrue("the revoke is not seen before the successor is");
        (await Tokens().HasLiveSessionAsync(subjectId, sessionId, DateTime.UtcNow)).Should().BeTrue();
    }
}
