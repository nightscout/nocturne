using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fido2NetLib;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An access request's complete step answers its failed-registration answer for an attestation
/// the WebAuthn verification refuses, including one with no authenticator response part.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class AccessRequestRegistrationRefusalIntegrationTests : ApiIntegrationTestBase
{
    public AccessRequestRegistrationRefusalIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task An_attestation_with_no_authenticator_response_is_answered_as_a_failed_registration()
    {
        var baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, ApiIntegrationTestFixture.TenantSlug, baseDomain);
        var displayName = $"Visitor {Guid.NewGuid():N}"[..20];

        var options = await client.PostAsJsonAsync("/api/auth/passkey/access-request/options",
            new { displayName, message = "hello" });
        options.StatusCode.Should().Be(HttpStatusCode.OK, await options.Content.ReadAsStringAsync());
        var challengeToken = JsonDocument.Parse(await options.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeToken").GetString();

        var credentialId = Guid.NewGuid().ToByteArray();
        var complete = await client.PostAsJsonAsync("/api/auth/passkey/access-request/complete", new
        {
            displayName,
            attestationResponseJson = JsonSerializer.Serialize(new AuthenticatorAttestationRawResponse
            {
                Id = System.Buffers.Text.Base64Url.EncodeToString(credentialId),
                RawId = credentialId,
                Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            }),
            challengeToken,
        });

        complete.StatusCode.Should().Be(HttpStatusCode.BadRequest, await complete.Content.ReadAsStringAsync());
        (await complete.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail.Should().Be("Passkey registration failed");
    }
}
