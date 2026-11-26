using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Device-code polls that overlap, on PostgreSQL under the runtime role and Row Level Security: two
/// polls of one code and a poll whose code is deleted before its stamp each answer a defined
/// RFC 8628 error rather than fail.
/// </summary>
[Trait("Category", "Integration")]
public class OAuthDeviceCodePollPostgresTests(OAuthDeviceCodePollPostgresTests.Database database)
    : IClassFixture<OAuthDeviceCodePollPostgresTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private const string DeviceCode = "device-code";
    private const string DeviceCodeHash = "device-code-hash";
    private const string ClientId = "app";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("oauth_device_code_poll");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="beforeStamp"/> once a poll has read its code and before it stamps the poll.</summary>
    private sealed class StageableRepository(INocturneDbContextFactory contexts, Func<Guid, Task> beforeStamp)
        : OAuthTokenRepository(contexts)
    {
        public override async Task<bool> RecordDevicePollAsync(Guid id, DateTime polledAt, CancellationToken ct)
        {
            await beforeStamp(id);
            return await base.RecordDevicePollAsync(id, polledAt, ct);
        }
    }

    /// <summary>Runs <paramref name="before"/> once, just before the statement that stamps a poll, however it is issued.</summary>
    private sealed class BeforePollStamp(Func<Task> before) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        private async Task RunAsync(DbCommand command)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("last_polled_at", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await before();
            }
        }
    }

    private NocturneDbContext Context(Guid tenant) => Context(tenant, null);

    private NocturneDbContext Context(Guid tenant, IInterceptor? staged) =>
        TestContexts.Context(database.AppConnectionString, tenant, interceptors: staged is null ? [] : [staged]);

    private sealed record Seeded(Guid Tenant, Guid CodeId, SuppliedContextFactory Contexts);

    private async Task<Seeded> SeedPendingCodeAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'device-code-poll-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"poll-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var contexts = new SuppliedContextFactory(() => Context(tenant));
        var codeId = await new OAuthTokenRepository(contexts).StoreDeviceCodeAsync(
            new NewDeviceCode(ClientId, DeviceCodeHash, "BCDFGHJK", ["glucose.read"], Now.UtcDateTime.AddMinutes(15), 5),
            CancellationToken.None);
        return new Seeded(tenant, codeId, contexts);
    }

    private static OAuthTokenService ServiceOver(IOAuthTokenRepository tokens, SuppliedContextFactory contexts)
    {
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.HashRefreshToken(DeviceCode)).Returns(DeviceCodeHash);
        return new OAuthTokenService(
            tokens,
            new OAuthGrantRepository(contexts),
            jwt.Object,
            Mock.Of<ISubjectService>(),
            Mock.Of<IOAuthGrantService>(),
            Mock.Of<IOAuthTokenRevocationCache>(),
            new FakeTimeProvider(Now),
            NullLogger<OAuthTokenService>.Instance);
    }

    [Fact]
    public async Task TwoPollsOfOnePendingCodeThatReadItBeforeEitherStampsBothAnswerAuthorizationPending()
    {
        var seeded = await SeedPendingCodeAsync();
        var arrived = 0;
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new StageableRepository(seeded.Contexts, _ =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                bothRead.SetResult();
            return bothRead.Task.WaitAsync(Eventually.DefaultPatience);
        });

        var polls = await Task.WhenAll(
            Task.Run(() => ServiceOver(tokens, seeded.Contexts).ExchangeDeviceCodeAsync(DeviceCode, ClientId)),
            Task.Run(() => ServiceOver(tokens, seeded.Contexts).ExchangeDeviceCodeAsync(DeviceCode, ClientId)));

        polls.Select(p => p.Error).Should().Equal("authorization_pending", "authorization_pending");
        var stored = await new OAuthTokenRepository(seeded.Contexts).FindDeviceCodeAsync(DeviceCodeHash, CancellationToken.None);
        stored!.LastPolledAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task APollWhoseCodeIsDeletedBeforeItsStampAnswersAuthorizationPending()
    {
        var seeded = await SeedPendingCodeAsync();
        var deleter = new OAuthTokenRepository(seeded.Contexts);
        var polling = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforePollStamp(() => deleter.DeleteDeviceCodeAsync(seeded.CodeId, CancellationToken.None))));

        var poll = await ServiceOver(new OAuthTokenRepository(polling), seeded.Contexts).ExchangeDeviceCodeAsync(DeviceCode, ClientId);

        poll.Error.Should().Be("authorization_pending");
        (await deleter.FindDeviceCodeAsync(DeviceCodeHash, CancellationToken.None)).Should().BeNull();
    }
}
