using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The per-account throttle on emailed links holds when many requests for one account arrive at
/// once, as they do from many client addresses.
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
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, tenantId, "Throttled Member");
        await ExecuteAsync(conn, "UPDATE subjects SET email = @email, email_confirmed_at = now() WHERE id = @id;",
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
            (await ScalarAsync<long>(conn,
                    "SELECT count(*) FROM login_codes WHERE subject_id = @id AND purpose = 'EmailRecovery';",
                    ("id", memberId)))
                .Should().Be(1);
        }
        finally
        {
            await ExecuteAsync(conn, "DELETE FROM login_codes WHERE subject_id = @id;", ("id", memberId));
            await ExecuteAsync(conn, "DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
