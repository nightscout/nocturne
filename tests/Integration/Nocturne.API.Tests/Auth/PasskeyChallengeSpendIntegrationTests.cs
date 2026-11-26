using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fido2NetLib;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A passkey challenge completes one sign-in: of any number of completions presenting one valid
/// assertion and its challenge, at once, exactly one signs in, and every other is answered as a
/// refused sign-in.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class PasskeyChallengeSpendIntegrationTests : ApiIntegrationTestBase
{
    public PasskeyChallengeSpendIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task Of_many_completions_of_one_challenge_at_once_exactly_one_signs_in()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Spend Member");
        using var key = await PasskeyAssertions.StoreKeyAsync(conn, memberId);
        var rpId = Fixture.Services.GetRequiredService<IOptions<Fido2Configuration>>().Value.RPID;
        var startedAt = DateTime.UtcNow;

        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
            var request = await PasskeyAssertions.SignInRequestAsync(client, memberId, key, rpId);

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                client.PostAsJsonAsync("/api/auth/passkey/login/complete", request)));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
            var refused = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
            refused.Should().HaveCount(7).And.AllSatisfy(r => r.StatusCode.Should().Be(HttpStatusCode.BadRequest));
            foreach (var response in refused)
            {
                JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("detail").GetString().Should().Be("Passkey authentication failed");
            }

            await using var rows = conn.CreateCommand();
            rows.CommandText = """
                SELECT count(*) FROM auth_audit_log
                WHERE event_type = 'failed_auth' AND success = false
                  AND error_message LIKE '%already been presented%' AND created_at >= @since;
                """;
            rows.Parameters.AddWithValue("since", startedAt);
            (await rows.ExecuteScalarAsync()).Should().Be(7L);
        }
        finally
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tenant_members WHERE subject_id = @id;";
            cmd.Parameters.AddWithValue("id", memberId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
