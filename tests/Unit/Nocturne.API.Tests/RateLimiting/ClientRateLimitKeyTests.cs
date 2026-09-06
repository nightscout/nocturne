using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Nocturne.API.RateLimiting;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.API.Tests.RateLimiting;

/// <summary>
/// Guards the partition the address-keyed rate-limit policies count against.
/// </summary>
/// <remarks>
/// A call made for a page or remote function arrives from the SvelteKit container, so the address
/// has to be carried in a header for these limits to bound one user rather than the deployment.
/// A header is writable by anyone the gateway admits, which is what the signature settles.
/// </remarks>
public class ClientRateLimitKeyTests
{
    private const string InstanceKey = "s3cret-instance-key";
    private const string GatewayAddress = "10.0.1.7";

    [Fact]
    public void ASignedAddress_PartitionsPerEndUser()
    {
        var key = KeyFor();

        var first = key.Resolve(Context("203.0.113.4"));
        var second = key.Resolve(Context("198.51.100.9"));

        first.Should().Be("203.0.113.4");
        second.Should().Be("198.51.100.9");
        first.Should().NotBe(second,
            "two users of the same SvelteKit container must not share one window");
    }

    [Fact]
    public void AnAddressWithNoSignature_IsIgnored()
    {
        var context = Context("203.0.113.4");
        context.Request.Headers.Remove(ServiceNames.Headers.ClientIpSignature);

        KeyFor().Resolve(context).Should().Be(GatewayAddress,
            "any caller the gateway admits can write the address header");
    }

    [Fact]
    public void AnAddressSignedWithAnotherKey_IsIgnored()
    {
        var context = Context("203.0.113.4", signWith: "not-the-instance-key");

        KeyFor().Resolve(context).Should().Be(GatewayAddress);
    }

    [Fact]
    public void ASignatureLiftedFromAnotherAddress_IsIgnored()
    {
        var context = Context("203.0.113.4");
        context.Request.Headers[ServiceNames.Headers.ClientIp] = "198.51.100.9";

        KeyFor().Resolve(context).Should().Be(GatewayAddress,
            "a captured signature must not be replayable onto a victim's address");
    }

    [Fact]
    public void AValueThatIsNotAnAddress_IsIgnored()
    {
        var context = Context("not-an-address");

        KeyFor().Resolve(context).Should().Be(GatewayAddress,
            "the partition key is an address, so a signed caller cannot mint arbitrary ones");
    }

    [Fact]
    public void WithNoInstanceKeyConfigured_TheAddressIsIgnored()
    {
        var context = Context("203.0.113.4");

        KeyFor(instanceKey: null).Resolve(context).Should().Be(GatewayAddress,
            "nothing can be verified without the key, so the header is not honoured");
    }

    [Fact]
    public void WithNoAddressPresented_ThePartitionIsTheConnection()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(GatewayAddress);

        KeyFor().Resolve(context).Should().Be(GatewayAddress);
    }

    [Fact]
    public void WithNeitherAnAddressNorAConnection_ThePartitionIsShared()
    {
        KeyFor().Resolve(new DefaultHttpContext()).Should().Be("unknown");
    }

    [Fact]
    public void ASignedIPv6Address_IsCanonicalized()
    {
        var context = Context("2001:DB8::1");

        KeyFor().Resolve(context).Should().Be("2001:db8::1",
            "one address written two ways must not buy two windows");
    }

    private static ClientRateLimitKey KeyFor(string? instanceKey = InstanceKey) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = instanceKey,
            })
            .Build());

    /// <summary>A request as the SSR server makes it: the gateway's peer address plus a signed client.</summary>
    private static HttpContext Context(string clientAddress, string signWith = InstanceKey)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(GatewayAddress);
        context.Request.Headers[ServiceNames.Headers.ClientIp] = clientAddress;
        context.Request.Headers[ServiceNames.Headers.ClientIpSignature] = Sign(clientAddress, signWith);
        return context;
    }

    private static string Sign(string value, string instanceKey) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(instanceKey),
            Encoding.UTF8.GetBytes(value)));
}
