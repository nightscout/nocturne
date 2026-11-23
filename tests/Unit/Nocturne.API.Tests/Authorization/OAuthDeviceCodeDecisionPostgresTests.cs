using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Approving or denying a device code on PostgreSQL under the runtime role and Row Level Security:
/// a code deleted after it was read answers as no longer pending, and of overlapping decisions on
/// one code exactly one is stored.
/// </summary>
[Trait("Category", "Integration")]
public class OAuthDeviceCodeDecisionPostgresTests(OAuthDeviceCodeDecisionPostgresTests.Database database)
    : IClassFixture<OAuthDeviceCodeDecisionPostgresTests.Database>
{
    private const string ClientId = "app";
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlySet<string> FullAccess = new HashSet<string> { Scope.FullAccess };

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("oauth_device_code_decision");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(create(scope));
    }

    /// <summary>Runs <paramref name="beforeDecision"/> once a decision has read its code and before it stores the decision.</summary>
    private sealed class StageableRepository(INocturneDbContextFactory contexts, Func<Guid, Task> beforeDecision)
        : OAuthTokenRepository(contexts)
    {
        public override async Task<bool> ApproveDeviceCodeAsync(
            Guid id, Guid subjectId, Guid grantId, DateTime now, CancellationToken ct)
        {
            await beforeDecision(id);
            return await base.ApproveDeviceCodeAsync(id, subjectId, grantId, now, ct);
        }

        public override async Task<bool> DenyDeviceCodeAsync(Guid id, DateTime now, CancellationToken ct)
        {
            await beforeDecision(id);
            return await base.DenyDeviceCodeAsync(id, now, ct);
        }
    }

    /// <summary>Runs <paramref name="before"/> once, just before the first statement that writes a device code, however it is issued.</summary>
    private sealed class BeforeDecisionWrite(Func<Task> before) : DbCommandInterceptor
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
                && command.CommandText.Contains("oauth_device_codes", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await before();
            }
        }
    }

    private NocturneDbContext Context(Guid tenant, IInterceptor? staged = null) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(staged is null
                ? [new TenantConnectionInterceptor()]
                : [new TenantConnectionInterceptor(), staged])
            .Options) { TenantId = tenant };

    private sealed record Seeded(Guid Tenant, Guid Grant, Guid Subject, Guid OtherSubject, string UserCode, Factory Contexts);

    private async Task<Seeded> SeedPendingCodeAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'device-code-decision-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"decision-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var userCode = tenant.ToString("N")[..8].ToUpperInvariant();
        var subject = Guid.CreateVersion7();
        var otherSubject = Guid.CreateVersion7();
        var client = Guid.CreateVersion7();
        var grant = Guid.CreateVersion7();
        await using (var db = Context(tenant))
        {
            db.Subjects.AddRange(
                new SubjectEntity { Id = subject, Name = "approver" },
                new SubjectEntity { Id = otherSubject, Name = "rival approver" });
            db.OAuthClients.Add(new OAuthClientEntity { Id = client, ClientId = ClientId, RedirectUris = "[]" });
            db.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = grant,
                ClientEntityId = client,
                SubjectId = subject,
                Scopes = ["glucose.read"],
                GrantType = "app",
            });
            await db.SaveChangesAsync();
        }

        var contexts = new Factory(t => Context(t), tenant);
        await new OAuthTokenRepository(contexts).StoreDeviceCodeAsync(
            new NewDeviceCode(ClientId, $"device-code-hash-{tenant:N}", userCode, ["glucose.read"], DateTime.UtcNow.AddMinutes(15), 5),
            CancellationToken.None);
        return new Seeded(tenant, grant, subject, otherSubject, userCode, contexts);
    }

    private static OAuthDeviceCodeService ServiceOver(IOAuthTokenRepository tokens, Guid grant)
    {
        var clients = new Mock<IOAuthClientService>();
        clients.Setup(c => c.GetClientAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo { Id = Guid.CreateVersion7(), ClientId = ClientId, DisplayName = "App" });
        var grants = new Mock<IOAuthGrantService>();
        grants.Setup(g => g.CreateOrUpdateGrantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthGrantInfo { Id = grant, ClientId = ClientId });
        return new OAuthDeviceCodeService(
            tokens,
            Mock.Of<IJwtService>(),
            clients.Object,
            grants.Object,
            NullLogger<OAuthDeviceCodeService>.Instance);
    }

    private static StageableRepository BothReadFirst(Factory contexts)
    {
        var arrived = 0;
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new StageableRepository(contexts, _ =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                bothRead.SetResult();
            return bothRead.Task.WaitAsync(BarrierTimeout);
        });
    }

    private static Task<DeviceCodeRecord?> StoredAsync(Seeded seeded) =>
        new OAuthTokenRepository(seeded.Contexts).FindDeviceCodeByUserCodeAsync(seeded.UserCode, CancellationToken.None);

    [Fact]
    public async Task ApprovingACodeDeletedAfterItWasReadAnswersNoLongerPending()
    {
        var seeded = await SeedPendingCodeAsync();
        var deleter = new OAuthTokenRepository(seeded.Contexts);
        var codeId = (await StoredAsync(seeded))!.Id;
        var approving = new Factory(
            tenant => Context(tenant, new BeforeDecisionWrite(() => deleter.DeleteDeviceCodeAsync(codeId, CancellationToken.None))),
            seeded.Tenant);

        var approved = await ServiceOver(new OAuthTokenRepository(approving), seeded.Grant)
            .ApproveDeviceCodeAsync(seeded.UserCode, seeded.Subject, FullAccess);

        approved.Should().BeFalse();
        (await StoredAsync(seeded)).Should().BeNull();
    }

    [Fact]
    public async Task DenyingACodeDeletedAfterItWasReadAnswersNoLongerPending()
    {
        var seeded = await SeedPendingCodeAsync();
        var deleter = new OAuthTokenRepository(seeded.Contexts);
        var codeId = (await StoredAsync(seeded))!.Id;
        var denying = new Factory(
            tenant => Context(tenant, new BeforeDecisionWrite(() => deleter.DeleteDeviceCodeAsync(codeId, CancellationToken.None))),
            seeded.Tenant);

        var denied = await ServiceOver(new OAuthTokenRepository(denying), seeded.Grant).DenyDeviceCodeAsync(seeded.UserCode);

        denied.Should().BeFalse();
        (await StoredAsync(seeded)).Should().BeNull();
    }

    [Fact]
    public async Task OfTwoApprovalsOfOneCodeThatReadItPendingExactlyOneIsStored()
    {
        var seeded = await SeedPendingCodeAsync();
        var tokens = BothReadFirst(seeded.Contexts);

        var approvals = await Task.WhenAll(
            Task.Run(() => ServiceOver(tokens, seeded.Grant).ApproveDeviceCodeAsync(seeded.UserCode, seeded.Subject, FullAccess)),
            Task.Run(() => ServiceOver(tokens, seeded.Grant).ApproveDeviceCodeAsync(seeded.UserCode, seeded.OtherSubject, FullAccess)));

        approvals.Count(a => a).Should().Be(1);
        var stored = await StoredAsync(seeded);
        stored!.ApprovedAt.Should().NotBeNull();
        stored.GrantId.Should().Be(seeded.Grant);
        stored.SubjectId.Should().Be(approvals[0] ? seeded.Subject : seeded.OtherSubject, "the stored approver is the one whose approval succeeded");
    }

    [Fact]
    public async Task OfTwoDenialsOfOneCodeThatReadItPendingExactlyOneIsStored()
    {
        var seeded = await SeedPendingCodeAsync();
        var tokens = BothReadFirst(seeded.Contexts);

        var denials = await Task.WhenAll(
            Task.Run(() => ServiceOver(tokens, seeded.Grant).DenyDeviceCodeAsync(seeded.UserCode)),
            Task.Run(() => ServiceOver(tokens, seeded.Grant).DenyDeviceCodeAsync(seeded.UserCode)));

        denials.Count(d => d).Should().Be(1);
        (await StoredAsync(seeded))!.DeniedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task OfAnApprovalAndADenialOfOneCodeThatReadItPendingExactlyOneIsStored()
    {
        var seeded = await SeedPendingCodeAsync();
        var tokens = BothReadFirst(seeded.Contexts);

        var decisions = await Task.WhenAll(
            Task.Run(() => ServiceOver(tokens, seeded.Grant).ApproveDeviceCodeAsync(seeded.UserCode, seeded.Subject, FullAccess)),
            Task.Run(() => ServiceOver(tokens, seeded.Grant).DenyDeviceCodeAsync(seeded.UserCode)));

        decisions.Count(d => d).Should().Be(1);
        var stored = await StoredAsync(seeded);
        (stored!.ApprovedAt.HasValue ^ stored.DeniedAt.HasValue).Should().BeTrue("the code holds one decision, not both");
    }
}
