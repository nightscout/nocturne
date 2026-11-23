using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Authorization;
using Nocturne.Core.Constants;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards which address a recorder is told the request came from. A call made for a page or remote
/// function arrives from the SvelteKit container, and the header that names the browser is writable
/// by anyone the gateway admits, so only a signed one may be recorded.
/// </summary>
public class ClientAddressResolverTests
{
    private const string InstanceKey = "s3cret-instance-key";
    private const string WebContainer = "172.30.0.4";

    [Fact]
    public void ASignedAddress_IsTheClientAddress()
    {
        Resolver().Resolve(Context("203.0.113.4")).Should().Be(IPAddress.Parse("203.0.113.4"));
    }

    [Fact]
    public void ASignedIPv6Address_IsReturnedWhole()
    {
        Resolver().Resolve(Context("2001:db8:aa:bb::1")).Should().Be(IPAddress.Parse("2001:db8:aa:bb::1"),
            "only a rate limit counts a network; a record names the address");
    }

    [Fact]
    public void AnAddressWithNoSignature_IsNotRecorded()
    {
        var context = Context("203.0.113.4");
        context.Request.Headers.Remove(ServiceNames.Headers.ClientIpSignature);

        Resolver().Resolve(context).Should().Be(IPAddress.Parse(WebContainer));
    }

    [Fact]
    public void AnAddressSignedWithAnotherKey_IsNotRecorded()
    {
        Resolver().Resolve(Context("203.0.113.4", signWith: "not-the-instance-key"))
            .Should().Be(IPAddress.Parse(WebContainer));
    }

    [Fact]
    public void ASignatureLiftedFromAnotherAddress_IsNotRecorded()
    {
        var context = Context("203.0.113.4");
        context.Request.Headers[ServiceNames.Headers.ClientIp] = "198.51.100.9";

        Resolver().Resolve(context).Should().Be(IPAddress.Parse(WebContainer),
            "a captured signature must not name another address");
    }

    [Fact]
    public void AValueThatIsNotAnAddress_IsNotRecorded()
    {
        Resolver().Resolve(Context("not-an-address")).Should().Be(IPAddress.Parse(WebContainer));
    }

    [Fact]
    public void WithNoInstanceKeyConfigured_TheHeaderIsNotHonoured()
    {
        Resolver(instanceKey: null).Resolve(Context("203.0.113.4")).Should().Be(IPAddress.Parse(WebContainer));
    }

    [Fact]
    public void WithNoHeader_TheClientAddressIsTheConnectionAddress()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(WebContainer);

        Resolver().Resolve(context).Should().Be(IPAddress.Parse(WebContainer));
    }

    [Fact]
    public void WithNeitherAHeaderNorAConnection_ThereIsNoAddress()
    {
        Resolver().Resolve(new DefaultHttpContext()).Should().BeNull();
    }

    private static ClientAddressResolver Resolver(string? instanceKey = InstanceKey) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = instanceKey,
            })
            .Build());

    private static HttpContext Context(string clientAddress, string signWith = InstanceKey)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(WebContainer);
        context.Request.Headers[ServiceNames.Headers.ClientIp] = clientAddress;
        context.Request.Headers[ServiceNames.Headers.ClientIpSignature] = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(signWith), Encoding.UTF8.GetBytes(clientAddress)));
        return context;
    }
}
