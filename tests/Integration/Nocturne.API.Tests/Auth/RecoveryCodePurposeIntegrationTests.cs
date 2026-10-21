using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A recovery code redeems only at recovery-mode redeem, never at the login handoff, and a refused
/// attempt does not spend it.
/// </summary>
[Trait("Category", "Integration")]
public class RecoveryCodePurposeIntegrationTests : ApiIntegrationTestBase
{
    public RecoveryCodePurposeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task A_recovery_code_is_refused_at_handoff_and_redeems_at_recovery_mode()
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);

        // The member still has a passkey at first, so the tenant is not in recovery mode and the
        // handoff answers on its merits rather than with the recovery-mode 503.
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Recovering Member");
        var code = $"recovery-{Guid.NewGuid():N}";
        await ExecuteAsync(conn, """
            INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, purpose, expires_at, created_at)
            VALUES (@id, @tenantId, @subjectId, @hash, 'Recovery', now() + interval '1 hour', now());
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", tenantId), ("subjectId", memberId),
            ("hash", HashUtils.Sha256Hex(code)));

        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

            var handoff = await client.PostAsJsonAsync("/api/auth/handoff", new { code });
            handoff.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            await ExecuteAsync(conn, "DELETE FROM passkey_credentials WHERE subject_id = @subjectId;",
                ("subjectId", memberId));

            var redeem = await client.PostAsJsonAsync("/api/auth/passkey/recovery-mode/redeem", new { code });
            redeem.StatusCode.Should().Be(HttpStatusCode.OK, await redeem.Content.ReadAsStringAsync());
        }
        finally
        {
            // Leaving a locked-out member behind would put the shared tenant into recovery mode.
            await ExecuteAsync(conn, "DELETE FROM tenant_members WHERE subject_id = @subjectId;",
                ("subjectId", memberId));
        }
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
