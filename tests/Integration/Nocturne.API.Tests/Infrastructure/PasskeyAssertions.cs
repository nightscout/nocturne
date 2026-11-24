using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Npgsql;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// A passkey sign-in through the real API for a member <see cref="AuthTestHelpers.SeedAuthenticatedSubjectAsync"/>
/// seeded: its stored credential is given a real public key, and each sign-in signs a fresh challenge.
/// </summary>
public static class PasskeyAssertions
{
    /// <summary>
    /// Gives the passkey seeded for <paramref name="subjectId"/> a real key and returns its private half.
    /// </summary>
    public static async Task<RSA> StoreKeyAsync(NpgsqlConnection conn, Guid subjectId)
    {
        var key = RSA.Create(2048);
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
        return key;
    }

    /// <summary>
    /// The body of a <c>login/complete</c> call: a fresh discoverable challenge, signed by
    /// <paramref name="key"/> for the passkey seeded for <paramref name="subjectId"/>.
    /// </summary>
    public static async Task<object> SignInRequestAsync(
        HttpClient client, Guid subjectId, RSA key, string rpId, bool showsRecoveryCodes = false)
    {
        var options = await client.PostAsync("/api/auth/passkey/login/discoverable/options", null);
        options.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await options.Content.ReadAsStringAsync()).RootElement;
        return new
        {
            assertionResponseJson = Assertion(
                Encoding.UTF8.GetBytes($"cred-{subjectId:N}"), subjectId, key, rpId,
                body.GetProperty("options").GetString()!),
            challengeToken = body.GetProperty("challengeToken").GetString(),
            showsRecoveryCodes,
        };
    }

    /// <summary>Signs in once with <see cref="SignInRequestAsync"/>.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, Guid subjectId, RSA key, string rpId, bool showsRecoveryCodes = false) =>
        await client.PostAsJsonAsync(
            "/api/auth/passkey/login/complete",
            await SignInRequestAsync(client, subjectId, key, rpId, showsRecoveryCodes));

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
