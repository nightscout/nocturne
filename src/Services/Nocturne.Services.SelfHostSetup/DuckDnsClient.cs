using System.Net;

namespace Nocturne.Services.SelfHostSetup;

public enum DuckDnsOutcome
{
    Ok,
    Refused,
    Unreachable,
}

/// <summary>
/// Points a DuckDNS name at this machine. DuckDNS answers every name below it with the same
/// address, so tenant and share subdomains resolve with no further records.
/// </summary>
public sealed class DuckDnsClient(HttpClient http)
{
    public async Task<DuckDnsOutcome> PointAtAsync(
        string name, string token, IPAddress address, CancellationToken cancellationToken)
    {
        // Clearing first drops any IPv6 record left from an earlier machine, which browsers would
        // otherwise prefer over the IPv4 address set below.
        var cleared = await UpdateAsync($"domains={name}&token={token}&clear=true", cancellationToken);
        if (cleared != DuckDnsOutcome.Ok)
            return cleared;

        return await UpdateAsync($"domains={name}&token={token}&ip={address}", cancellationToken);
    }

    private async Task<DuckDnsOutcome> UpdateAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var body = await http.GetStringAsync($"https://www.duckdns.org/update?{query}", cancellationToken);
            return body.Trim() == "OK" ? DuckDnsOutcome.Ok : DuckDnsOutcome.Refused;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return DuckDnsOutcome.Unreachable;
        }
    }
}
