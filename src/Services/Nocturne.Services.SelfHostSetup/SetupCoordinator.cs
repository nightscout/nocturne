using System.Net;
using Nocturne.Core.Constants;

namespace Nocturne.Services.SelfHostSetup;

/// <param name="ConfigDirectory">The Compose project directory holding <c>.env</c>.</param>
/// <param name="PublicIp">The address DNS must answer with.</param>
public sealed record SetupOptions(string ConfigDirectory, IPAddress PublicIp);

public abstract record SetupOutcome
{
    public sealed record Completed(string BaseDomain) : SetupOutcome;
    public sealed record ClaimRejected : SetupOutcome;
    public sealed record LockedOut : SetupOutcome;
    public sealed record AlreadyComplete : SetupOutcome;
    public sealed record Invalid(string Message) : SetupOutcome;
    public sealed record DuckDnsFailed(DuckDnsOutcome Reason) : SetupOutcome;
    public sealed record DnsPending(string Domain, IPAddress PublicIp, bool ApexReady, bool WildcardReady) : SetupOutcome;
}

/// <summary>
/// Settles the instance's base domain once. Nocturne binds passkeys to it, so it is written only
/// after the claim code and the DNS both check out, and never rewritten by this service.
/// </summary>
public sealed class SetupCoordinator(ClaimGuard guard, DuckDnsClient duckDns, DnsProbe dns, SetupOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsComplete => File.Exists(Path.Combine(options.ConfigDirectory, ServiceNames.SelfHost.CompletionMarker));

    public Task<SetupOutcome> UseDuckDnsAsync(string? claim, string? name, string? token, CancellationToken cancellationToken) =>
        RunAsync(claim, async () =>
        {
            if (!DomainInput.TryParseDuckDnsName(name, out var subdomain))
                return new SetupOutcome.Invalid("That is not a DuckDNS name. Use the part before .duckdns.org, e.g. myname.");
            if (!DomainInput.TryParseDuckDnsToken(token, out var duckDnsToken))
                return new SetupOutcome.Invalid("That is not a DuckDNS token. Copy the token shown at the top of duckdns.org once signed in.");

            var result = await duckDns.PointAtAsync(subdomain, duckDnsToken, options.PublicIp, cancellationToken);
            if (result != DuckDnsOutcome.Ok)
                return new SetupOutcome.DuckDnsFailed(result);

            var baseDomain = $"{subdomain}.duckdns.org";
            await CompleteAsync(new Dictionary<string, string>
            {
                ["BASE_DOMAIN"] = baseDomain,
                ["COMPOSE_PROFILES"] = ServiceNames.SelfHost.DuckDnsProfile,
                ["DUCKDNS_SUBDOMAIN"] = subdomain,
                ["DUCKDNS_TOKEN"] = duckDnsToken,
            }, cancellationToken);
            return new SetupOutcome.Completed(baseDomain);
        }, cancellationToken);

    public Task<SetupOutcome> UseCustomDomainAsync(string? claim, string? domain, CancellationToken cancellationToken) =>
        RunAsync(claim, async () =>
        {
            if (!DomainInput.TryParseBaseDomain(domain, out var baseDomain))
                return new SetupOutcome.Invalid("Enter a hostname such as nocturne.example.com: no address, port or path.");

            var readiness = await dns.CheckAsync(baseDomain, options.PublicIp, cancellationToken);
            if (!readiness.Ready)
                return new SetupOutcome.DnsPending(baseDomain, options.PublicIp, readiness.ApexReady, readiness.WildcardReady);

            await CompleteAsync(new Dictionary<string, string>
            {
                ["BASE_DOMAIN"] = baseDomain,
                ["COMPOSE_PROFILES"] = "",
            }, cancellationToken);
            return new SetupOutcome.Completed(baseDomain);
        }, cancellationToken);

    private async Task<SetupOutcome> RunAsync(string? claim, Func<Task<SetupOutcome>> configure, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsComplete)
                return new SetupOutcome.AlreadyComplete();

            return guard.Check(claim) switch
            {
                ClaimResult.Accepted => await configure(),
                ClaimResult.LockedOut => new SetupOutcome.LockedOut(),
                _ => new SetupOutcome.ClaimRejected(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CompleteAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var envPath = Path.Combine(options.ConfigDirectory, ".env");
        var updated = EnvFile.Apply(await File.ReadAllTextAsync(envPath, cancellationToken), values);

        var staging = envPath + ".setup";
        await File.WriteAllTextAsync(staging, updated, cancellationToken);
        File.Move(staging, envPath, overwrite: true);

        await File.WriteAllTextAsync(Path.Combine(options.ConfigDirectory, ServiceNames.SelfHost.CompletionMarker), "", cancellationToken);
    }
}
