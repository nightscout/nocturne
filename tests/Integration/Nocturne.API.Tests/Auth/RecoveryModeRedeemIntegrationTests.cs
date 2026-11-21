using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An administrator-issued recovery code is spent only by a redemption that starts a recovery
/// session. Every refusal leaves it unspent and answers as an unknown code does, and of several
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
    }

    [Theory]
    [InlineData(Refusal.MemberCanStillSignIn)]
    [InlineData(Refusal.NoLongerAMember)]
    [InlineData(Refusal.Deactivated)]
    [InlineData(Refusal.AdministratorCodeForAnOperatorOnlyMember)]
    public async Task A_refused_redemption_leaves_the_code_unspent(Refusal refusal)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);

        var (memberId, _) = refusal == Refusal.MemberCanStillSignIn
            ? await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Recovering Member")
            : await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, tenantId, "Recovering Member");
        var (codeId, code) = await IssueCodeAsync(conn, tenantId, memberId, "Recovery");

        switch (refusal)
        {
            case Refusal.NoLongerAMember:
                await ExecuteAsync(conn, "DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
                break;
            case Refusal.Deactivated:
                await ExecuteAsync(conn, "UPDATE subjects SET is_active = false WHERE id = @id;", ("id", memberId));
                break;
            case Refusal.AdministratorCodeForAnOperatorOnlyMember:
                await ExecuteAsync(conn, "UPDATE subjects SET is_platform_admin = true WHERE id = @id;", ("id", memberId));
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

            (await ScalarAsync<bool>(conn, "SELECT consumed_at IS NULL FROM login_codes WHERE id = @id;", ("id", codeId)))
                .Should().BeTrue("a refused redemption does not spend the code");
            (await ScalarAsync<long>(conn,
                    "SELECT count(*) FROM auth_audit_log WHERE event_type = 'login_handoff' AND target_subject_id = @id;",
                    ("id", memberId)))
                .Should().Be(0, "a refused redemption is not recorded as one that succeeded");
        }
        finally
        {
            // Leaving a locked-out member behind would put the shared tenant into recovery mode.
            await ExecuteAsync(conn, "DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    [Fact]
    public async Task A_code_presented_twice_at_once_redeems_once()
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
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
            (await ScalarAsync<bool>(conn, "SELECT consumed_at IS NOT NULL FROM login_codes WHERE id = @id;", ("id", codeId)))
                .Should().BeTrue();
        }
        finally
        {
            await ExecuteAsync(conn, "DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    private static async Task<(Guid Id, string Code)> IssueCodeAsync(
        NpgsqlConnection conn, Guid tenantId, Guid subjectId, string purpose)
    {
        var id = Guid.CreateVersion7();
        var code = $"recovery-{Guid.NewGuid():N}";
        await ExecuteAsync(conn, """
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
