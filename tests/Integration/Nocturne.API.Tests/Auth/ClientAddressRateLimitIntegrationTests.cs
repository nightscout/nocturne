using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An address-keyed rate limit counts one IPv6 network as one client, through the pipeline: the
/// signed address header, the limiter middleware and the endpoint.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ClientAddressRateLimitIntegrationTests : ApiIntegrationTestBase
{
    private const string Policy = "login-handoff";

    public ClientAddressRateLimitIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task AttemptsFromEveryAddressInOneIPv6Network_MeetThePerClientCeiling()
    {
        var permitLimit = ServiceRegistrationExtensions.ClientAddressPolicies.Single(p => p.Policy == Policy).PermitLimit;
        var network = FreshNetwork();

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= permitLimit + 1; i++)
        {
            using var response = await RedeemFromAsync($"{network}:{i:x}");
            statuses.Add(response.StatusCode);
        }

        statuses.Take(permitLimit).Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.BadRequest),
            "each attempt under the ceiling reaches the endpoint and is refused as an unknown code");
        statuses.Last().Should().Be(HttpStatusCode.TooManyRequests,
            "{0} distinct addresses in one /64 are one client", permitLimit + 1);

        using var neighbour = await RedeemFromAsync($"{FreshNetwork()}:1");
        neighbour.StatusCode.Should().Be(HttpStatusCode.BadRequest, "another /64 is another client");
    }

    /// <summary>The first four groups of an IPv6 /64 no other test presents.</summary>
    private static string FreshNetwork()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        return $"2001:db8:{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}:0:0";
    }

    private async Task<HttpResponseMessage> RedeemFromAsync(string address)
    {
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        client.DefaultRequestHeaders.Remove(ServiceNames.Headers.ClientIp);
        client.DefaultRequestHeaders.Remove(ServiceNames.Headers.ClientIpSignature);
        client.DefaultRequestHeaders.Add(ServiceNames.Headers.ClientIp, address);
        client.DefaultRequestHeaders.Add(ServiceNames.Headers.ClientIpSignature, Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(ApiIntegrationTestFixture.InstanceKey),
                Encoding.UTF8.GetBytes(address))));

        return await client.PostAsJsonAsync("/api/auth/handoff", new { code = "not-a-login-code" });
    }
}
