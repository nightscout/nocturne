<script lang="ts">
    import { Cloud } from "@lucide/svelte";
    import NextSteps from "$lib/components/docs/NextSteps.svelte";
    import SupportNocturne from "$lib/components/docs/SupportNocturne.svelte";
    import CodeBlock from "$lib/components/docs/CodeBlock.svelte";
    import installScript from "$lib/release/oracle-cloud/oracle-cloud-install.sh?raw";

    const runCommand =
        "BASE_DOMAIN=nocturne.example.com bash <(curl -fsSL https://github.com/nightscout/nocturne/releases/latest/download/oracle-cloud-install.sh)";
</script>

<div class="max-w-3xl">
    <div class="flex items-center gap-4 mb-4">
        <div class="w-12 h-12 rounded-lg bg-red-500/15 flex items-center justify-center shrink-0">
            <Cloud class="w-7 h-7 text-red-600" />
        </div>
        <h1 class="text-4xl font-bold tracking-tight">Oracle Cloud</h1>
    </div>
    <p class="text-lg text-muted-foreground mb-8">
        Run Nocturne on Oracle Cloud's Always Free tier. One command in the browser sets
        up the server, the network, HTTPS and Nocturne itself. No SSH or Docker knowledge
        needed.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">What you need</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>
            An <a href="https://www.oracle.com/cloud/free/" class="text-primary hover:underline">Oracle Cloud account</a>.
            Sign-up asks for a payment card to verify your identity. The resources this guide
            creates are within the Always Free allowance, so nothing is charged.
        </li>
        <li>
            A domain name. Nocturne gives every site its own subdomain, so the DNS provider
            must support wildcard records. If you do not have a domain, a free one from deSEC
            works well; see below.
        </li>
    </ul>

    <h2 class="text-2xl font-bold mt-8 mb-4">No domain yet? Get one from deSEC</h2>
    <p class="text-muted-foreground mb-4">
        <a href="https://desec.io" class="text-primary hover:underline">deSEC</a> is a non-profit
        DNS provider that gives out free names under <strong>dedyn.io</strong>. With one of those,
        the installer creates the DNS records for you and there is nothing to configure by hand.
    </p>
    <ol class="list-decimal list-inside space-y-2 text-muted-foreground mb-8">
        <li>Create an account at desec.io and confirm your email address.</li>
        <li>
            Under <strong>Domains</strong>, add a dynDNS domain and pick a name. Your domain is
            then <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">yourname.dedyn.io</code>.
        </li>
        <li>
            Under <strong>Token management</strong>, create a token and copy it. The installer
            asks for it and hides what you paste. You can delete the token afterwards.
        </li>
    </ol>
    <p class="text-muted-foreground mb-8">
        The same works for a domain you already own if its DNS is hosted at deSEC. If it is
        hosted elsewhere, you add two records by hand in Step 3.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 1: Open Cloud Shell</h2>
    <p class="text-muted-foreground mb-8">
        Sign in to the <a href="https://cloud.oracle.com" class="text-primary hover:underline">Oracle Cloud console</a>
        and click the terminal icon in the top-right toolbar, labelled <strong>Developer tools</strong>,
        then <strong>Cloud Shell</strong>. A terminal opens at the bottom of the page. It is already
        signed in as you, so there is nothing to install or configure.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 2: Run the installer</h2>
    <p class="text-muted-foreground mb-4">
        Paste this into Cloud Shell, replacing
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">nocturne.example.com</code>
        with your domain. Nocturne will answer on that name and every site you create gets a
        subdomain under it, so pick something you are happy to keep.
    </p>
    <CodeBlock code={runCommand} class="mb-4" />
    <p class="text-muted-foreground mb-4">
        The script creates a network, reserves a public IP address, and starts a server.
        Early on it asks for a deSEC token. Paste one and the DNS records are created for you;
        press Enter instead and it prints two records for you to create by hand. Either way it
        keeps working while the server boots and waits for the records before it requests
        certificates.
    </p>
    <details class="mb-8">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">What the script does</summary>
        <ul class="list-disc list-inside space-y-1 text-sm text-muted-foreground mt-2 mb-2">
            <li>Creates a virtual network with ports 80 and 443 open, in your home region</li>
            <li>Reserves a public IP address so it never changes</li>
            <li>Generates an SSH key in your Cloud Shell home if you do not have one</li>
            <li>Starts an Ampere A1 server with 2 cores and 12 GB of memory, retrying when Oracle has no free capacity</li>
            <li>On the server: opens the firewall, installs Docker, downloads the Nocturne release bundle, generates the database passwords and starts everything once DNS resolves</li>
        </ul>
        <CodeBlock code={installScript} class="mt-2" maxHeight="400px" />
    </details>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 3: Add the DNS records</h2>
    <p class="text-muted-foreground mb-4">
        Skip this step if you gave the installer a deSEC token. Otherwise, at your domain
        provider, create two <strong>A</strong> records pointing at the IP address the script
        printed:
    </p>
    <CodeBlock code={"nocturne.example.com      A   <the IP address>\n*.nocturne.example.com    A   <the IP address>"} class="mb-4" />
    <p class="text-muted-foreground mb-8">
        The second one is a wildcard. It makes every site's subdomain resolve without a record
        each. If you use Cloudflare, leave the proxy off for both records, shown as a grey cloud.
        Cloudflare's proxy cannot carry the certificates Nocturne issues for its own subdomains.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 4: Create your first site</h2>
    <p class="text-muted-foreground mb-8">
        Once the script reports that Nocturne is answering, open
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">https://your-domain</code>
        in a browser. You are taken to the setup page to name your first site and register a
        passkey, which is your phone's or computer's built-in unlock. Register a second device
        as soon as you can, because there is no password to fall back on.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">If Oracle has no free capacity</h2>
    <p class="text-muted-foreground mb-8">
        Ampere A1 servers are popular and regions often run out. The script tries every
        availability domain in your region, and keeps retrying for 30 minutes. If it gives up,
        run the same command again later. Everything it already created is reused. A smaller
        server is easier to place:
    </p>
    <CodeBlock code={"OCPUS=1 MEMORY_GB=6 " + runCommand} class="mb-8" />

    <h2 class="text-2xl font-bold mt-8 mb-4">Keeping it free</h2>
    <p class="text-muted-foreground mb-8">
        Oracle reclaims Always Free servers on trial accounts that look idle for a week. A
        Nocturne server collecting readings is rarely that quiet, but to remove the risk you
        can upgrade the account to Pay As You Go. Always Free resources stay free after the
        upgrade, and are then exempt from reclamation. Nothing in this guide exceeds the
        free allowance.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Afterwards</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>
            The script ends by printing your <strong>instance key</strong>. It is the master
            credential for this installation, used for administrative API access and account
            recovery. Save it in a password manager. Running the script again shows it again.
        </li>
        <li>Updates are automatic. Watchtower checks for new Nocturne images daily.</li>
        <li>Nocturne starts again by itself when the server reboots.</li>
        <li>
            To get a shell on the server, run the SSH command the script printed. Everything
            lives in <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">/opt/nocturne</code>.
        </li>
        <li>
            Nothing backs up your data automatically. In the Oracle console, under Block
            Storage, you can take a backup of the server's boot volume at any time. The free
            tier includes five volume backups, so take them by hand and delete old ones rather
            than attaching a scheduled policy.
        </li>
        <li>
            To remove everything, terminate the instance in the Oracle console, then delete the
            reserved public IP and the <strong>nocturne</strong> virtual cloud network.
        </li>
    </ul>

    <h2 class="text-2xl font-bold mt-8 mb-4">Next Steps</h2>
    <NextSteps />

    <SupportNocturne />
</div>
