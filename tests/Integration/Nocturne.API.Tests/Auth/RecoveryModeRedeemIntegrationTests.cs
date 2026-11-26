using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An administrator's or the operator's recovery code is spent only by a redemption that starts a
/// recovery session. Every refusal leaves it unspent and answers as an unknown code does, and of several
/// presentations of one code at once, one redeems it.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class RecoveryModeRedeemIntegrationTests : ApiIntegrationTestBase
{
    private const string RedeemPath = "/api/auth/passkey/recovery-mode/redeem";

    public RecoveryModeRedeemIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public enum Refusal
    {
        MemberCanStillSignIn,
        NoLongerAMember,
        Deactivated,
        AdministratorCodeForAnOperatorOnlyMember,
        SystemSubject,
        DemoSubject,
    }

    [Theory]
    [InlineData(Refusal.MemberCanStillSignIn, "Recovery")]
    [InlineData(Refusal.NoLongerAMember, "Recovery")]
    [InlineData(Refusal.Deactivated, "Recovery")]
    [InlineData(Refusal.AdministratorCodeForAnOperatorOnlyMember, "Recovery")]
    [InlineData(Refusal.NoLongerAMember, "OperatorRecovery")]
    [InlineData(Refusal.Deactivated, "OperatorRecovery")]
    [InlineData(Refusal.SystemSubject, "OperatorRecovery")]
    [InlineData(Refusal.DemoSubject, "OperatorRecovery")]
    public async Task A_refused_redemption_leaves_the_code_unspent(Refusal refusal, string purpose)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);

        var (memberId, _) = refusal == Refusal.MemberCanStillSignIn
            ? await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Recovering Member")
            : await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, tenantId, "Recovering Member");
        var (codeId, code) = await IssueCodeAsync(conn, tenantId, memberId, purpose);

        switch (refusal)
        {
            case Refusal.NoLongerAMember:
                await conn.ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
                break;
            case Refusal.Deactivated:
                await conn.ExecuteAsync("UPDATE subjects SET is_active = false WHERE id = @id;", ("id", memberId));
                break;
            case Refusal.AdministratorCodeForAnOperatorOnlyMember:
                await conn.ExecuteAsync("UPDATE subjects SET is_platform_admin = true WHERE id = @id;", ("id", memberId));
                break;
            case Refusal.SystemSubject:
                await conn.ExecuteAsync("UPDATE subjects SET is_system_subject = true WHERE id = @id;", ("id", memberId));
                break;
            case Refusal.DemoSubject:
                await conn.ExecuteAsync("UPDATE subjects SET is_demo_subject = true WHERE id = @id;", ("id", memberId));
                break;
        }

        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

            var unknown = await client.PostAsJsonAsync(RedeemPath, new { code = $"unknown-{Guid.NewGuid():N}" });
            var refused = await client.PostAsJsonAsync(RedeemPath, new { code });

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            Detail(await refused.Content.ReadAsStringAsync())
                .Should().Be(Detail(await unknown.Content.ReadAsStringAsync()));
            RecoveryCookies(refused).Should().BeEmpty("no recovery session may start");

            (await conn.ScalarAsync<bool>("SELECT consumed_at IS NULL FROM login_codes WHERE id = @id;", ("id", codeId)))
                .Should().BeTrue("a refused redemption does not spend the code");
            (await conn.ScalarAsync<long>("SELECT count(*) FROM auth_audit_log WHERE event_type = 'login_handoff' AND target_subject_id = @id;",
                    ("id", memberId)))
                .Should().Be(0, "a refused redemption is not recorded as one that succeeded");
        }
        finally
        {
            // A locked-out member left behind would show on the shared tenant's recovery list.
            await conn.ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    [Fact]
    public async Task An_operators_code_redeems_for_a_member_who_still_has_a_passkey()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);

        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Recovering Member");
        var (_, code) = await IssueCodeAsync(conn, tenantId, memberId, "OperatorRecovery");

        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

            var redeemed = await client.PostAsJsonAsync(RedeemPath, new { code });

            redeemed.StatusCode.Should().Be(HttpStatusCode.OK, await redeemed.Content.ReadAsStringAsync());
            RecoveryCookies(redeemed).Should().ContainSingle();
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    [Fact]
    public async Task A_code_presented_twice_at_once_redeems_once()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);

        var (memberId, _) = await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, tenantId, "Recovering Member");
        var (codeId, code) = await IssueCodeAsync(conn, tenantId, memberId, "Recovery");

        try
        {
            using var first = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
            using var second = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

            var responses = await Task.WhenAll(
                first.PostAsJsonAsync(RedeemPath, new { code }),
                second.PostAsJsonAsync(RedeemPath, new { code }));

            responses.Select(r => r.StatusCode).Should()
                .BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.BadRequest]);
            RecoveryCookies(responses.Single(r => r.StatusCode == HttpStatusCode.OK)).Should().ContainSingle();
            // Starting the recovery session issues a code, which prunes spent ones, so the row may be gone.
            (await conn.ScalarAsync<long>("SELECT count(*) FROM login_codes WHERE id = @id AND consumed_at IS NULL;", ("id", codeId)))
                .Should().Be(0);
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    private static async Task<(Guid Id, string Code)> IssueCodeAsync(
        NpgsqlConnection conn, Guid tenantId, Guid subjectId, string purpose)
    {
        var id = Guid.CreateVersion7();
        var code = $"recovery-{Guid.NewGuid():N}";
        await conn.ExecuteAsync("""
            INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, purpose, expires_at, created_at)
            VALUES (@id, @tenantId, @subjectId, @hash, @purpose, now() + interval '1 hour', now());
            """,
            ("id", id), ("tenantId", tenantId), ("subjectId", subjectId),
            ("hash", HashUtils.Sha256Hex(code)), ("purpose", purpose));
        return (id, code);
    }

    private static IEnumerable<string> RecoveryCookies(HttpResponseMessage response) =>
        (response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [])
            .Where(c => c.StartsWith(".Nocturne.RecoverySession=", StringComparison.Ordinal));

    private static string? Detail(string problemJson) =>
        JsonDocument.Parse(problemJson).RootElement.GetProperty("detail").GetString();
}
