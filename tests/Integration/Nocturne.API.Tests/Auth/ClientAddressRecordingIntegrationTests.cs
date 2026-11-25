using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The address a persisted row records, through the real pipeline: the one the SvelteKit server
/// signed, or the connection's when the header is not signed. The rows come from three recorders at
/// different depths: the credential handler's last-used stamp (before the audit context exists), the
/// mutation audit (from the audit context) and an auth audit row (passed in by a controller).
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ClientAddressRecordingIntegrationTests : ApiIntegrationTestBase
{
    public ClientAddressRecordingIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task A_signed_client_address_is_the_address_every_recorder_stores()
    {
        var address = FreshAddress();
        using var client = CreateClientPresenting(address, signWith: ApiIntegrationTestFixture.InstanceKey);

        await MakeRecordedRequestsAsync(client);

        (await RecordedAddressesAsync()).Should().Be(
            new Recorded(address, address, address),
            "the web container relays the request, so the signed address is the client's");
    }

    [Theory]
    [InlineData("not-the-instance-key")]
    [InlineData(null)]
    public async Task A_client_address_that_is_not_signed_by_the_instance_key_is_never_stored(string? signWith)
    {
        var forged = FreshAddress();
        var forwarded = FreshForwardedAddress();
        using var client = CreateClientPresenting(forged, signWith);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwarded);

        await MakeRecordedRequestsAsync(client);

        (await RecordedAddressesAsync()).Should().Be(
            new Recorded(forwarded, forwarded, forwarded),
            "the unverified header is ignored, and the address is the connection's as the forwarded-headers middleware left it");
    }

    private static async Task MakeRecordedRequestsAsync(HttpClient client)
    {
        using var mutation = await client.PostAsJsonAsync("/api/v4/lab-results/hba1c", new
        {
            measuredAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), valuePercent = 6.8,
        });
        mutation.StatusCode.Should().Be(HttpStatusCode.OK, await mutation.Content.ReadAsStringAsync());

        using var refused = await client.PostAsJsonAsync(
            "/api/auth/totp/login", new { stepUpToken = "not-a-step-up-token", code = "123456" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record Recorded(string? LastUsed, string? Mutation, string? AuthAudit);

    private async Task<Recorded> RecordedAddressesAsync()
    {
        await using var conn = await OpenPostgresAsync();

        var lastUsed = await Eventually.ValueAsync(
            "the grant's last used address to be recorded",
            () => conn.ScalarAsync<string?>(
                "SELECT last_used_ip FROM oauth_grants WHERE subject_id = @subject AND last_used_ip IS NOT NULL",
                ("subject", Fixture.OwnerSubjectId)),
            step: TimeSpan.FromMilliseconds(100));

        var mutation = await conn.ScalarAsync<string?>(
            "SELECT ip_address FROM mutation_audit_log WHERE action = 'create' AND actor_subject_id = @subject ORDER BY created_at DESC LIMIT 1",
            ("subject", Fixture.OwnerSubjectId));
        var authAudit = await conn.ScalarAsync<string?>(
            "SELECT ip_address FROM auth_audit_log WHERE event_type = @type AND details->>'method' = 'totp'",
            ("type", AuthAuditEventType.FailedAuth));

        return new Recorded(lastUsed, mutation, authAudit);
    }

    /// <summary>An address no other test presents, from the documentation range.</summary>
    private static string FreshAddress() => $"203.0.113.{RandomNumberGenerator.GetInt32(1, 255)}";

    /// <summary>An address from a second documentation range, so it cannot equal the presented one.</summary>
    private static string FreshForwardedAddress() => $"198.51.100.{RandomNumberGenerator.GetInt32(1, 255)}";

    private HttpClient CreateClientPresenting(string address, string? signWith)
    {
        var client = CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove(ServiceNames.Headers.ClientIp);
        client.DefaultRequestHeaders.Remove(ServiceNames.Headers.ClientIpSignature);
        client.DefaultRequestHeaders.Add(ServiceNames.Headers.ClientIp, address);
        if (signWith is not null)
        {
            client.DefaultRequestHeaders.Add(ServiceNames.Headers.ClientIpSignature, Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(signWith), Encoding.UTF8.GetBytes(address))));
        }

        return client;
    }
}
