using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The per-account throttle on emailed links holds when many requests for one account arrive at
/// once, as they do from many client addresses, and a request waiting on a redemption's open claim
/// holds nothing that redemption's recovery session needs.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class EmailedLinkThrottleIntegrationTests : ApiIntegrationTestBase
{
    private const string Address = "throttle@example.test";

    public EmailedLinkThrottleIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private sealed class RecordingDelivery : IAccountLinkDelivery
    {
        public ConcurrentBag<AccountLinkMessage> Sent { get; } = [];
        public bool IsConfigured => true;

        public Task<bool> DeliverAsync(AccountLinkMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Concurrent_requests_for_one_account_send_one_link()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, tenantId, "Throttled Member");
        await conn.ExecuteAsync("UPDATE subjects SET email = @email, email_confirmed_at = now() WHERE id = @id;",
            ("email", Address), ("id", memberId));
        var delivery = new RecordingDelivery();
        var tenant = new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false);

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => WithTenantScopeAsync(async services =>
            {
                var links = new AccountLinkService(
                    services.GetRequiredService<INocturneDbContextFactory>(),
                    services.GetRequiredService<ILoginCodeService>(),
                    services.GetRequiredService<ISubjectRepository>(),
                    delivery,
                    Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }), TimeProvider.System);
                await links.SendRecoveryLinksAsync(tenant, Address, new AuthAuditActor(null, "test"), null, null, default);
                return true;
            })));

            delivery.Sent.Should().ContainSingle();
            (await conn.ScalarAsync<long>("SELECT count(*) FROM login_codes WHERE subject_id = @id AND purpose = 'EmailRecovery';",
                    ("id", memberId)))
                .Should().Be(1);
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM login_codes WHERE subject_id = @id;", ("id", memberId));
            await conn.ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    [Fact]
    public async Task A_link_request_waiting_on_a_redemptions_claim_does_not_stall_that_redemptions_recovery_session()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, tenantId, "Redeeming Member");
        var claimedId = await InsertCodeAsync(conn, tenantId, memberId, "EmailRecovery", Address, createdMinutesAgo: 3, consumed: false);
        var spentId = await InsertCodeAsync(conn, tenantId, memberId, "Login", null, createdMinutesAgo: 0, consumed: true);

        await using var claimConn = await OpenPostgresAsync();
        await using var claim = await claimConn.BeginTransactionAsync();
        using var cancel = new CancellationTokenSource();
        Task<LoginCode?>? mint = null;
        Task<LoginCode>? session = null;

        try
        {
            await using (var cmd = claimConn.CreateCommand())
            {
                cmd.Transaction = claim;
                cmd.CommandText = "UPDATE login_codes SET consumed_at = now() WHERE id = @id;";
                cmd.Parameters.AddWithValue("id", claimedId);
                await cmd.ExecuteNonQueryAsync();
            }

            mint = WithTenantScopeAsync(async services =>
            {
                await using var db = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancel.Token);
                return await services.GetRequiredService<ILoginCodeService>().IssueEmailedAsync(
                    db, memberId, LoginCodePurpose.EmailRecovery, Address, null, null,
                    new AuthAuditActor(null, "test"), cancel.Token);
            });
            await PostgresLockWaits.UntilBlockedByAsync(
                await GetPostgresConnectionStringAsync(), claimConn.ProcessID, mint);

            session = WithTenantScopeAsync(async services =>
            {
                await using var db = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancel.Token);
                return await services.GetRequiredService<ILoginCodeService>().IssueAsync(
                    db, memberId, LoginCodePurpose.RecoverySession, null, null,
                    new AuthAuditActor(null, "test"), cancel.Token);
            });
            (await session.WaitAsync(Eventually.DefaultPatience)).Should().NotBeNull();
            mint.IsCompleted.Should().BeFalse("the link request still waits on the redemption's claim");

            await claim.CommitAsync();

            (await mint.WaitAsync(Eventually.DefaultPatience)).Should().NotBeNull();
            (await conn.ScalarAsync<long>("SELECT count(*) FROM login_codes WHERE id = @id;", ("id", spentId)))
                .Should().Be(0);
        }
        finally
        {
            await claim.DisposeAsync();
            cancel.Cancel();
            await Task.WhenAny(Task.WhenAll(new Task?[] { mint, session }.OfType<Task>()), Task.Delay(Eventually.DefaultPatience));
            await conn.ExecuteAsync("DELETE FROM login_codes WHERE subject_id = @id;", ("id", memberId));
            await conn.ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    private static async Task<Guid> InsertCodeAsync(
        NpgsqlConnection conn, Guid tenantId, Guid subjectId, string purpose, string? email, int createdMinutesAgo, bool consumed)
    {
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, purpose, email, expires_at, created_at, consumed_at)
            VALUES (@id, @tenantId, @subjectId, @hash, @purpose, @email, now() + interval '20 minutes',
                now() - make_interval(mins => @ago), CASE WHEN @consumed THEN now() END);
            """,
            ("id", id), ("tenantId", tenantId), ("subjectId", subjectId), ("hash", $"{id:N}"),
            ("purpose", purpose), ("email", (object?)email ?? DBNull.Value), ("ago", createdMinutesAgo), ("consumed", consumed));
        return id;
    }
}
