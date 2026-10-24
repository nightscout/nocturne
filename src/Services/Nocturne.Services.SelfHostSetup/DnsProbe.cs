using System.Net;
using System.Net.Sockets;

namespace Nocturne.Services.SelfHostSetup;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

public sealed record DnsReadiness(bool ApexReady, bool WildcardReady)
{
    public bool Ready => ApexReady && WildcardReady;
}

/// <summary>
/// Checks a custom domain's records before Caddy asks Let's Encrypt for certificates: a challenge
/// that fails on a record still propagating counts against the issuance rate limit.
/// </summary>
public sealed class DnsProbe(IDnsResolver resolver)
{
    /// <summary>
    /// An arbitrary label under the domain; it resolves here only through the <c>*.</c> record
    /// that tenant and share subdomains need.
    /// </summary>
    public const string WildcardProbeLabel = "nocturne-dns-check";

    public async Task<DnsReadiness> CheckAsync(string domain, IPAddress expected, CancellationToken cancellationToken)
    {
        var apex = await resolver.ResolveAsync(domain, cancellationToken);
        var wildcard = await resolver.ResolveAsync($"{WildcardProbeLabel}.{domain}", cancellationToken);
        return new DnsReadiness(apex.Contains(expected), wildcard.Contains(expected));
    }
}
