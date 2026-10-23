#pragma warning disable ASPIREPIPELINES003 // Experimental container image APIs

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Nocturne.Aspire.Host.Publishing;
using Nocturne.Core.Constants;

namespace Nocturne.Aspire.Host;

/// <summary>
/// The first-boot services of the one-click cloud images. Both sit behind Compose profiles, so a
/// plain <c>docker compose up</c> never starts them: the image's systemd unit runs the
/// <c>setup</c> profile once, and the setup page writes <c>COMPOSE_PROFILES=duckdns</c> to
/// <c>.env</c> when the owner picks DuckDNS.
/// </summary>
internal static class SelfHostSetup
{
    public static void AddSelfHostSetup(this IDistributedApplicationBuilder builder)
    {
        var claimCode = builder.AddParameter("setup-claim-code", secret: true)
            .WithPublishMetadata(
                "Setup claim code",
                "Written by the first-boot script: the cloud instance ID the owner types into the setup page");
        var publicIp = builder.AddParameter("public-ip", "")
            .WithPublishMetadata(
                "Public IP address",
                "Written by the first-boot script from the cloud metadata service; the address DNS must point at");

        builder
            .AddProject<Projects.Nocturne_Services_SelfHostSetup>("selfhost-setup", launchProfileName: null)
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithEnvironment("SETUP_CLAIM_CODE", claimCode)
            .WithEnvironment("PUBLIC_IP", publicIp)
            .WithEnvironment("SETUP_CONFIG_DIR", "/config")
            .WithRemoteImageName("ghcr.io/nightscout/nocturne/nocturne-selfhost-setup")
            .WithRemoteImageTag("latest")
            .WithPublishImageMetadata(
                imageLabel: "Setup image",
                imageDefault: "ghcr.io/nightscout/nocturne/nocturne-selfhost-setup:latest")
            .PublishAsDockerComposeService((_, service) =>
            {
                service.Profiles.Add(ServiceNames.SelfHost.SetupProfile);
                // Runs once, before Caddy exists, so it takes port 80 itself; it exits when done.
                service.Ports.Add("80:8080");
                service.Restart = "no";
                // The Compose project directory, where it rewrites .env and drops the completion marker.
                service.Volumes.Add(new() { Name = "selfhost-config", Type = "bind", Source = ".", Target = "/config" });
            });

        var duckDnsSubdomain = builder.AddParameter("duckdns-subdomain", "");
        var duckDnsToken = builder.AddParameter("duckdns-token", "", secret: true);

        // DuckDNS follows the machine's address; without an updater a stop and start on a cloud
        // with ephemeral addresses would strand the hostname, and every passkey bound to it.
        builder
            .AddContainer("duckdns", "lscr.io/linuxserver/duckdns", "latest")
            .WithEnvironment("SUBDOMAINS", duckDnsSubdomain)
            .WithEnvironment("TOKEN", duckDnsToken)
            .WithEnvironment("UPDATE_IP", "ipv4")
            .PublishAsDockerComposeService((_, service) =>
                service.Profiles.Add(ServiceNames.SelfHost.DuckDnsProfile));
    }
}
