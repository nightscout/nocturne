using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Spend Member");
        var credentialId = Encoding.UTF8.GetBytes($"cred-{memberId:N}");
        using var key = RSA.Create(2048);
        await StorePublicKeyAsync(conn, memberId, key);
        var rpId = Fixture.Services.GetRequiredService<IOptions<Fido2Configuration>>().Value.RPID;
        var startedAt = DateTime.UtcNow;

        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
            var options = await client.PostAsync("/api/auth/passkey/login/discoverable/options", null);
            options.EnsureSuccessStatusCode();
            var body = JsonDocument.Parse(await options.Content.ReadAsStringAsync()).RootElement;
            var request = new
            {
                assertionResponseJson = Assertion(
                    credentialId, memberId, key, rpId, body.GetProperty("options").GetString()!),
                challengeToken = body.GetProperty("challengeToken").GetString(),
            };

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

    private static async Task StorePublicKeyAsync(NpgsqlConnection conn, Guid subjectId, RSA key)
    {
        var publicKey = key.ExportParameters(includePrivateParameters: false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE passkey_credentials SET public_key = @key WHERE subject_id = @id;";
        cmd.Parameters.AddWithValue("key", new Fido2NetLib.Cbor.CborMap
        {
            { 1, 3 },
            { 3, -257 },
            { -1, publicKey.Modulus! },
            { -2, publicKey.Exponent! },
        }.Encode());
        cmd.Parameters.AddWithValue("id", subjectId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// An assertion signed by <paramref name="key"/> over <paramref name="optionsJson"/>'s challenge,
    /// from a browser on the rpId's own host, by an authenticator that reports counter 0.
    /// </summary>
    private static string Assertion(byte[] credentialId, Guid subjectId, RSA key, string rpId, string optionsJson)
    {
        using var options = JsonDocument.Parse(optionsJson);
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.get",
            challenge = options.RootElement.GetProperty("challenge").GetString(),
            origin = $"https://{rpId}",
        });
        var authenticatorData = new Fido2NetLib.Objects.AuthenticatorData(
            SHA256.HashData(Encoding.UTF8.GetBytes(rpId)),
            Fido2NetLib.Objects.AuthenticatorFlags.UP | Fido2NetLib.Objects.AuthenticatorFlags.UV,
            signCount: 0, null, null).ToByteArray();

        return JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
        {
            Id = System.Buffers.Text.Base64Url.EncodeToString(credentialId),
            RawId = credentialId,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authenticatorData,
                Signature = key.SignData(
                    [.. authenticatorData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                ClientDataJson = clientData,
                UserHandle = subjectId.ToByteArray(),
            },
        });
    }
}
