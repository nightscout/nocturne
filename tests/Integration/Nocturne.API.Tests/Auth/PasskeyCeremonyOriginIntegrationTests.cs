using System.Net.Http.Json;
using System.Text;
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
/// The relying party's registered configuration, the options each ceremony builds its own from, is
/// the same after any number of passkey sign-ins from different hosts.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class PasskeyCeremonyOriginIntegrationTests : ApiIntegrationTestBase
{
    public PasskeyCeremonyOriginIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task Sign_ins_from_many_hosts_leave_the_configured_origins_as_they_were()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Origin Member");
        var credentialId = Encoding.UTF8.GetBytes($"cred-{memberId:N}");

        var shared = Fixture.Services.GetRequiredService<IOptions<Fido2Configuration>>().Value;
        var rpId = shared.RPID;
        var configured = string.Join(",", shared.FullyQualifiedOrigins.Order());
        var presented = Enumerable.Range(0, 20).Select(i => $"https://origin-{i}-{Guid.NewGuid():N}.{rpId}").ToList();

        try
        {
            foreach (var origin in presented)
            {
                using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
                var options = await client.PostAsync("/api/auth/passkey/login/discoverable/options", null);
                options.EnsureSuccessStatusCode();
                var challengeToken = JsonDocument.Parse(await options.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("challengeToken").GetString();

                var complete = await client.PostAsJsonAsync("/api/auth/passkey/login/complete", new
                {
                    assertionResponseJson = Assertion(credentialId, origin),
                    challengeToken,
                });

                complete.IsSuccessStatusCode.Should().BeFalse("the assertion carries no valid signature");
            }

            shared.Origins.Should().NotContain(presented);
            string.Join(",", shared.FullyQualifiedOrigins.Order()).Should().Be(configured);
        }
        finally
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tenant_members WHERE subject_id = @id;";
            cmd.Parameters.AddWithValue("id", memberId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// An assertion naming <paramref name="credentialId"/> from a browser at <paramref name="origin"/>,
    /// whose authenticator data does not parse.
    /// </summary>
    private static string Assertion(byte[] credentialId, string origin) =>
        JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
        {
            Id = System.Buffers.Text.Base64Url.EncodeToString(credentialId),
            RawId = credentialId,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = [0],
                Signature = [0],
                ClientDataJson = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "webauthn.get",
                    challenge = "AAAAAAAAAAAAAAAAAAAAAA",
                    origin,
                }),
            },
        });
}
