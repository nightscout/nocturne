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
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Approving or denying a device code on PostgreSQL under the runtime role and Row Level Security:
/// a code deleted after it was read answers as no longer pending, and of overlapping decisions on
/// one code exactly one is stored. A decision that loses leaves the grant as it found it.
/// </summary>
[Trait("Category", "Integration")]
public class OAuthDeviceCodeDecisionPostgresTests(OAuthDeviceCodeDecisionPostgresTests.Database database)
    : IClassFixture<OAuthDeviceCodeDecisionPostgresTests.Database>
{
    private const string ClientId = "app";
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
        TestContexts.Context(database.AppConnectionString, tenant, interceptors: staged is null ? [] : [staged]);

    private sealed record Seeded(Guid Tenant, Guid Client, Guid Grant, Guid Subject, Guid OtherSubject, string UserCode, SuppliedContextFactory Contexts);

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
            db.TenantMembers.AddRange(
                new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject },
                new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = otherSubject });
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

        var contexts = new SuppliedContextFactory(() => Context(tenant));
        await new OAuthTokenRepository(contexts).StoreDeviceCodeAsync(
            new NewDeviceCode(ClientId, $"device-code-hash-{tenant:N}", userCode, [Scope.GlucoseRead, Scope.TreatmentsRead], DateTime.UtcNow.AddMinutes(15), 5),
            CancellationToken.None);
        return new Seeded(tenant, client, grant, subject, otherSubject, userCode, contexts);
    }

    private static OAuthDeviceCodeService ServiceOver(SuppliedContextFactory contexts, Seeded seeded)
    {
        var clients = new Mock<IOAuthClientService>();
        clients.Setup(c => c.GetClientAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo { Id = seeded.Client, ClientId = ClientId, DisplayName = "App" });
        return new OAuthDeviceCodeService(
            new OAuthTokenRepository(contexts),
            Mock.Of<IJwtService>(),
            clients.Object,
            contexts,
            new GrantConsentService(NullLogger<GrantConsentService>.Instance),
            TimeProvider.System,
            NullLogger<OAuthDeviceCodeService>.Instance);
    }

    private SuppliedContextFactory ApprovedByARivalFirst(Seeded seeded, Guid codeId)
    {
        var rival = new OAuthTokenRepository(seeded.Contexts);
        return new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeDecisionWrite(() =>
                rival.ApproveDeviceCodeAsync(codeId, seeded.OtherSubject, seeded.Grant, DateTime.UtcNow, CancellationToken.None))));
    }

    private SuppliedContextFactory BothReachTheDecisionWrite(Seeded seeded)
    {
        var arrived = 0;
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeDecisionWrite(() =>
            {
                if (Interlocked.Increment(ref arrived) == 2)
                    bothArrived.SetResult();
                return bothArrived.Task.WaitAsync(Eventually.DefaultPatience);
            })));
    }

    private async Task<List<OAuthGrantEntity>> GrantsAsync(Seeded seeded)
    {
        await using var db = Context(seeded.Tenant);
        return await db.OAuthGrants.AsNoTracking().Where(g => g.ClientEntityId == seeded.Client).ToListAsync();
    }

    private static Task<DeviceCodeRecord?> StoredAsync(Seeded seeded) =>
        new OAuthTokenRepository(seeded.Contexts).FindDeviceCodeByUserCodeAsync(seeded.UserCode, CancellationToken.None);

    [Fact]
    public async Task ApprovingACodeDeletedAfterItWasReadAnswersNoLongerPending()
    {
        var seeded = await SeedPendingCodeAsync();
        var deleter = new OAuthTokenRepository(seeded.Contexts);
        var codeId = (await StoredAsync(seeded))!.Id;
        var approving = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeDecisionWrite(() => deleter.DeleteDeviceCodeAsync(codeId, CancellationToken.None))));

        var approved = await ServiceOver(approving, seeded)
            .ApproveDeviceCodeAsync(seeded.UserCode, seeded.OtherSubject, FullAccess);

        approved.Should().BeFalse();
        (await StoredAsync(seeded)).Should().BeNull();
    }

    [Fact]
    public async Task ApprovingACodeDeletedAfterItWasReadCreatesNoGrant()
    {
        var seeded = await SeedPendingCodeAsync();
        var deleter = new OAuthTokenRepository(seeded.Contexts);
        var codeId = (await StoredAsync(seeded))!.Id;
        var approving = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeDecisionWrite(() => deleter.DeleteDeviceCodeAsync(codeId, CancellationToken.None))));

        await ServiceOver(approving, seeded).ApproveDeviceCodeAsync(seeded.UserCode, seeded.OtherSubject, FullAccess);

        (await GrantsAsync(seeded)).Should().ContainSingle().Which.Id.Should().Be(seeded.Grant);
    }

    [Fact]
    public async Task DenyingACodeDeletedAfterItWasReadAnswersNoLongerPending()
    {
        var seeded = await SeedPendingCodeAsync();
        var deleter = new OAuthTokenRepository(seeded.Contexts);
        var codeId = (await StoredAsync(seeded))!.Id;
        var denying = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeDecisionWrite(() => deleter.DeleteDeviceCodeAsync(codeId, CancellationToken.None))));

        var denied = await ServiceOver(denying, seeded).DenyDeviceCodeAsync(seeded.UserCode);

        denied.Should().BeFalse();
        (await StoredAsync(seeded)).Should().BeNull();
    }

    [Fact]
    public async Task AnApprovalThatLosesToARivalCreatesNoGrant()
    {
        var seeded = await SeedPendingCodeAsync();
        var codeId = (await StoredAsync(seeded))!.Id;

        var approved = await ServiceOver(ApprovedByARivalFirst(seeded, codeId), seeded)
            .ApproveDeviceCodeAsync(seeded.UserCode, seeded.OtherSubject, FullAccess);

        approved.Should().BeFalse();
        (await GrantsAsync(seeded)).Should().ContainSingle().Which.Id.Should().Be(seeded.Grant);
        (await StoredAsync(seeded))!.GrantId.Should().Be(seeded.Grant, "the rival's approval stands");
    }

    [Fact]
    public async Task AnApprovalThatLosesToARivalDoesNotWidenTheGrantTheApproverHolds()
    {
        var seeded = await SeedPendingCodeAsync();
        var codeId = (await StoredAsync(seeded))!.Id;

        var approved = await ServiceOver(ApprovedByARivalFirst(seeded, codeId), seeded)
            .ApproveDeviceCodeAsync(seeded.UserCode, seeded.Subject, FullAccess);

        approved.Should().BeFalse();
        var grant = (await GrantsAsync(seeded)).Should().ContainSingle().Subject;
        grant.Id.Should().Be(seeded.Grant);
        grant.Scopes.Should().BeEquivalentTo([Scope.GlucoseRead]);
        grant.ApprovedByDeviceCode.Should().BeFalse();
    }

    [Fact]
    public async Task AnApprovalThatWinsWidensTheGrantTheApproverHoldsAndLinksItToTheCode()
    {
        var seeded = await SeedPendingCodeAsync();

        var approved = await ServiceOver(seeded.Contexts, seeded)
            .ApproveDeviceCodeAsync(seeded.UserCode, seeded.Subject, FullAccess);

        approved.Should().BeTrue();
        var grant = (await GrantsAsync(seeded)).Should().ContainSingle().Subject;
        grant.Id.Should().Be(seeded.Grant);
        grant.Scopes.Should().BeEquivalentTo([Scope.GlucoseRead, Scope.TreatmentsRead]);
        var stored = await StoredAsync(seeded);
        stored!.GrantId.Should().Be(grant.Id);
        stored.SubjectId.Should().Be(seeded.Subject);
    }

    [Fact]
    public async Task AnApprovalThatWinsCreatesTheGrantOfAnApproverWithNoneAndLinksItToTheCode()
    {
        var seeded = await SeedPendingCodeAsync();

        var approved = await ServiceOver(seeded.Contexts, seeded)
            .ApproveDeviceCodeAsync(seeded.UserCode, seeded.OtherSubject, FullAccess);

        approved.Should().BeTrue();
        var created = (await GrantsAsync(seeded)).Should().ContainSingle(g => g.SubjectId == seeded.OtherSubject).Subject;
        created.Scopes.Should().BeEquivalentTo([Scope.GlucoseRead, Scope.TreatmentsRead]);
        (await StoredAsync(seeded))!.GrantId.Should().Be(created.Id);
    }

    [Fact]
    public async Task OfTwoApprovalsOfOneCodeThatReadItPendingExactlyOneIsStoredAndOnlyItsGrantIsWritten()
    {
        var seeded = await SeedPendingCodeAsync();
        var contexts = BothReachTheDecisionWrite(seeded);

        var approvals = await Task.WhenAll(
            Task.Run(() => ServiceOver(contexts, seeded).ApproveDeviceCodeAsync(seeded.UserCode, seeded.Subject, FullAccess)),
            Task.Run(() => ServiceOver(contexts, seeded).ApproveDeviceCodeAsync(seeded.UserCode, seeded.OtherSubject, FullAccess)));

        approvals.Count(a => a).Should().Be(1);
        var subjectWon = approvals[0];
        var stored = await StoredAsync(seeded);
        stored!.ApprovedAt.Should().NotBeNull();
        stored.SubjectId.Should().Be(subjectWon ? seeded.Subject : seeded.OtherSubject, "the stored approver is the one whose approval succeeded");

        var grants = await GrantsAsync(seeded);
        grants.Should().HaveCount(subjectWon ? 1 : 2, "the loser's grant is not written");
        grants.Should().ContainSingle(g => g.Id == stored.GrantId);
        grants.Single(g => g.Id == seeded.Grant).Scopes.Should().BeEquivalentTo(
            subjectWon ? [Scope.GlucoseRead, Scope.TreatmentsRead] : [Scope.GlucoseRead]);
    }

    [Fact]
    public async Task OfTwoDenialsOfOneCodeThatReadItPendingExactlyOneIsStored()
    {
        var seeded = await SeedPendingCodeAsync();
        var contexts = BothReachTheDecisionWrite(seeded);

        var denials = await Task.WhenAll(
            Task.Run(() => ServiceOver(contexts, seeded).DenyDeviceCodeAsync(seeded.UserCode)),
            Task.Run(() => ServiceOver(contexts, seeded).DenyDeviceCodeAsync(seeded.UserCode)));

        denials.Count(d => d).Should().Be(1);
        (await StoredAsync(seeded))!.DeniedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task OfAnApprovalAndADenialOfOneCodeThatReadItPendingExactlyOneIsStored()
    {
        var seeded = await SeedPendingCodeAsync();
        var contexts = BothReachTheDecisionWrite(seeded);

        var decisions = await Task.WhenAll(
            Task.Run(() => ServiceOver(contexts, seeded).ApproveDeviceCodeAsync(seeded.UserCode, seeded.Subject, FullAccess)),
            Task.Run(() => ServiceOver(contexts, seeded).DenyDeviceCodeAsync(seeded.UserCode)));

        decisions.Count(d => d).Should().Be(1);
        var stored = await StoredAsync(seeded);
        (stored!.ApprovedAt.HasValue ^ stored.DeniedAt.HasValue).Should().BeTrue("the code holds one decision, not both");
        var grant = (await GrantsAsync(seeded)).Should().ContainSingle().Subject;
        grant.Scopes.Should().BeEquivalentTo(
            decisions[0] ? [Scope.GlucoseRead, Scope.TreatmentsRead] : [Scope.GlucoseRead],
            "a denied code leaves the grant the approver held");
    }
}
