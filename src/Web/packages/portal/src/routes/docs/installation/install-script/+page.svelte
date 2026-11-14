<script lang="ts">
    import { ArtworkHero } from "water-ui";
    import { resolve } from "$app/paths";
    import { DOCS_HERO } from "#lib/data/docs-artwork.js";
    import Callout from "@nocturne/cms/components/Callout.svelte";
    import SystemRequirements from "#lib/components/docs/SystemRequirements.svelte";
    import NextSteps from "#lib/components/docs/NextSteps.svelte";
    import SupportNocturne from "#lib/components/docs/SupportNocturne.svelte";
    import CodeBlock from "#lib/components/docs/CodeBlock.svelte";
    import InstallGuideBar from "#lib/components/docs/install/InstallGuideBar.svelte";
    import StepHeading from "#lib/components/docs/install/StepHeading.svelte";
    import Domain from "#lib/components/docs/install/Domain.svelte";
    import SiteLink from "#lib/components/docs/install/SiteLink.svelte";
    import DuckDnsSteps from "#lib/components/docs/install/DuckDnsSteps.svelte";
    import { createInstallGuide } from "#lib/components/docs/install/guide.svelte.js";
    import installScript from "#lib/release/install/install.sh?raw";

    const hero = DOCS_HERO["installation/install-script"];

    const guide = createInstallGuide({ id: "install-script" });
    // The installer sets the DNS record of a name under duckdns.org itself.
    const duckDns = $derived(guide.host?.toLowerCase().endsWith(".duckdns.org") ?? false);

    // Commands and example values, not user-facing text.
    // @wc-ignore
    const FALLBACK_DOMAIN = "nocturne.example.com";
    const domainName = $derived(guide.host ?? FALLBACK_DOMAIN);
    // @wc-ignore
    const runCommand = $derived(
        [
            "curl -fsSL https://github.com/nightscout/nocturne/releases/latest/download/install.sh -o install.sh",
            `sudo BASE_DOMAIN=${domainName} bash install.sh`,
        ].join("\n"),
    );
    // @wc-ignore
    const dnsRecords = $derived(
        `${domainName}      A   <the IP address of your server>\n*.${domainName}    A   <the IP address of your server>`,
    );
    // @wc-ignore
    const repairCommand = "sudo /usr/local/sbin/nocturne-up";
    // @wc-ignore
    const updateCommand = "cd /opt/nocturne && sudo docker compose pull && sudo docker compose up -d";
    // @wc-ignore
    const logsCommand = "sudo journalctl -u nocturne\ncd /opt/nocturne && sudo docker compose logs";

    const STEPS = [
        { id: "step-1", title: "Point your domain at the server" },
        { id: "step-2", title: "Run the installer" },
        { id: "step-3", title: "Create your first site" },
    ];

    // @wc-ignore
    const SUMMARY = "text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground";
    // @wc-ignore
    const CODE = "text-xs bg-muted/50 px-1.5 py-0.5 rounded";
</script>

<div class="max-w-3xl">
    <ArtworkHero
        artwork={hero.artwork}
        palette={hero.palette}
        motion="auto"
        autoplay="once"
        size={400}
        wrap={hero.wrap ?? true}
    />
    <h1 class="text-4xl font-bold tracking-tight mb-4">Install script</h1>
    <p class="text-lg text-muted-foreground mb-8">
        One script installs Nocturne on a Linux server. It installs Docker, makes the keys
        and passwords, gets the HTTPS certificates and starts Nocturne. You do not need to
        know Docker.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Before you start</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-4">
        <li>
            A Linux server where you can run commands as root. A VPS, a home server, or a
            Raspberry Pi 4 or newer with a 64-bit system is sufficient. Ubuntu and Debian are
            the usual selection. The server must use systemd.
        </li>
        <li>
            A domain name. Nocturne gives each site its own subdomain, so the DNS provider
            must support wildcard records. If you do not have a domain, a free one from
            DuckDNS is sufficient.
        </li>
        <li>
            Ports <strong>80</strong> and <strong>443</strong> open from the internet to the
            server. On a home network, forward the two ports on your router to the server.
        </li>
    </ul>
    <SystemRequirements />

    <InstallGuideBar steps={STEPS} placeholder="yourname.duckdns.org" />

    {#if !guide.host || duckDns}
        <h2 id="duckdns" class="text-2xl font-bold mt-8 mb-4 scroll-mt-36">
            {#if duckDns}Your DuckDNS token{:else}No domain yet? Get one from DuckDNS{/if}
        </h2>
        <DuckDnsSteps />
        <p class="text-muted-foreground mb-8">
            {#if duckDns}
                The installer uses the token in
                <a href="#step-2" class="text-primary hover:underline">Step 2</a>.
            {:else}
                Then enter your domain in the bar at the top of the page. If you already own a
                domain, enter that domain instead and ignore this section.
            {/if}
        </p>
    {/if}

    <StepHeading id="step-1" installStep="1">Step 1: Point your domain at the server</StepHeading>
    {#if !guide.host || duckDns}
        <p class="text-muted-foreground {duckDns ? 'mb-8' : 'mb-4'}">
            {#if !guide.host}<strong class="text-foreground">A domain under duckdns.org:</strong>{/if}
            Do nothing here. The installer sets the record when you give it your DuckDNS token
            in <a href="#step-2" class="text-primary hover:underline">Step 2</a>.
        </p>
    {/if}
    {#if !duckDns}
        <p class="text-muted-foreground mb-4">
            {#if !guide.host}<strong class="text-foreground">Any other domain:</strong>{/if}
            Create two <strong>A</strong> records at your domain provider. Your server provider
            shows the public IP address of the server.
        </p>
        <CodeBlock code={dnsRecords} class="mb-4" />
        <p class="text-muted-foreground mb-8">
            The second one is a wildcard. It makes every site's subdomain resolve without a record
            each. If you use Cloudflare, leave the proxy off for both records, shown as a gray cloud.
            Cloudflare's proxy cannot carry the certificates Nocturne issues for its own subdomains.
            You can also do this step later: the installer shows the two records with the address
            that it found, and waits for them.
        </p>
    {/if}

    <StepHeading id="step-2" installStep="2">Step 2: Run the installer</StepHeading>
    {#if guide.host}
        <p class="text-muted-foreground mb-4">
            Connect to your server and run these two commands. They contain your domain.
            Nocturne will answer on <Domain fallback={FALLBACK_DOMAIN} />. Every site you create
            gets a subdomain under it.
        </p>
    {:else}
        <p class="text-muted-foreground mb-4">
            Enter your domain in the bar at the top of the page. The commands below then
            contain it. Then connect to your server and run the two commands.
        </p>
    {/if}
    <CodeBlock code={runCommand} class="mb-4" />
    {#if !guide.host || duckDns}
        <p class="text-muted-foreground mb-4">
            {#if !guide.host}<strong class="text-foreground">A domain under duckdns.org:</strong>{/if}
            The installer asks early on for your DuckDNS token.
            <strong>Nothing appears on screen as you paste it</strong>. The token is hidden. The
            installer tells you how many characters it read, so you can see that the paste landed.
            If DuckDNS does not accept it, you can paste another.
        </p>
    {/if}
    <p class="text-muted-foreground mb-4">
        The installer waits ten minutes for the DNS records. If they are not ready then, the
        installer stops waiting, and the server continues to wait by itself. Nocturne starts
        when the records resolve.
    </p>
    <p class="text-muted-foreground mb-4">
        The installer ends by printing your <strong>instance key</strong>. It is the master
        credential for this installation, used for administrative API access and account
        recovery. Save it in a password manager.
    </p>
    <details class="mb-4">
        <summary class={SUMMARY}>What the script does</summary>
        <ul class="list-disc list-inside space-y-1 text-sm text-muted-foreground mt-2 mb-2">
            <li>Finds the public IP address of the server</li>
            <li>Points your DuckDNS name at that address when you give it the token. For any other domain, it prints the two records to create by hand</li>
            <li>Opens ports 80 and 443 in a ufw firewall on the server</li>
            <li>Installs Docker on a server that does not have it</li>
            <li>Downloads the Nocturne release bundle to <code class={CODE}>/opt/nocturne</code></li>
            <li>Makes the keys and the database passwords, and writes them to <code class={CODE}>/opt/nocturne/.env</code>. It does not change a value that exists</li>
            <li>Starts Nocturne when DNS resolves</li>
            <li>Stops, and changes nothing, if Docker already holds Nocturne data from an installation that it did not make</li>
            <li>Keeps itself on the server as <code class={CODE}>nocturne-up</code>, with a systemd unit that starts Nocturne after a restart</li>
        </ul>
        <CodeBlock code={installScript} class="mt-2" maxHeight="400px" />
    </details>
    <Callout type="info" title="If anything goes wrong, run it again">
        <p>
            Running the installer a second time is the fix for almost any failure. Each step
            checks before it acts, and it does not change keys or passwords that exist. It
            remembers your domain. The installer is also on the server as a command:
        </p>
        <CodeBlock code={repairCommand} class="mt-2" />
    </Callout>

    <StepHeading id="step-3" installStep="3">Step 3: Create your first site</StepHeading>
    <p class="text-muted-foreground mb-8">
        When the script prints <strong>Done</strong>, open <SiteLink /> in a browser.
        You are taken to the setup page to name your first site and create the
        owner account, with a passkey or a sign-in provider. A passkey is your phone's or
        computer's built-in unlock. If you choose one, register a second device as soon as you
        can, because there is no password to fall back on.
    </p>

    <h2 data-install-step="next-steps" class="text-2xl font-bold mt-12 mb-4">Next steps</h2>
    <NextSteps />

    <h2 class="text-2xl font-bold mt-12 mb-4">After the installation</h2>
    <details class="mb-3">
        <summary class={SUMMARY}>Update Nocturne</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Watchtower is included and checks for image updates daily, except for Caddy and
            Postgres. To update everything, including Caddy and Postgres:
        </p>
        <CodeBlock code={updateCommand} class="mb-4" />
        <p class="text-muted-foreground mb-4">
            Watchtower updates images, not the compose file. To get the compose file of the
            latest release, run <code class={CODE}>{repairCommand}</code>.
        </p>
    </details>
    <details class="mb-3">
        <summary class={SUMMARY}>Files, logs and restarts</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Everything is in <code class={CODE}>/opt/nocturne</code>. The keys and passwords are
            in <code class={CODE}>.env</code> there. Keep a copy of that file in a location that
            is not this server. Nocturne starts again by itself when the server restarts.
            To see the logs:
        </p>
        <CodeBlock code={logsCommand} class="mb-4" />
        <p class="text-muted-foreground mb-4">
            The <a href={resolve("/docs/installation/docker-compose")} class="text-primary hover:underline">Docker Compose guide</a>
            has the procedures for a lost <code class={CODE}>.env</code> file and for
            troubleshooting. Run its commands in <code class={CODE}>/opt/nocturne</code>.
        </p>
    </details>
    <details class="mb-8">
        <summary class={SUMMARY}>When to use a different guide</summary>
        <ul class="list-disc list-inside space-y-2 text-muted-foreground mt-2 mb-4">
            <li>
                You operate your own reverse proxy, or you want the PostgreSQL data in a host
                directory: use the
                <a href={resolve("/docs/installation/docker-compose")} class="text-primary hover:underline">Docker Compose guide</a>.
            </li>
            <li>
                You manage your server with Portainer: use the
                <a href={resolve("/docs/installation/portainer")} class="text-primary hover:underline">Portainer guide</a>.
            </li>
            <li>
                You do not have a server: the
                <a href={resolve("/docs/installation/oracle-cloud")} class="text-primary hover:underline">Oracle Cloud guide</a>
                makes a free one and runs this same installer on it.
            </li>
        </ul>
    </details>

    <SupportNocturne />
</div>
