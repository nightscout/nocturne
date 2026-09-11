<script lang="ts">
    import { Cloud } from "@lucide/svelte";
    import Callout from "@nocturne/cms/components/Callout.svelte";
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
            Sign-up asks for a payment card to verify your identity. Always Free gives you
            2 Ampere cores, 12 GB of memory and 200 GB of storage; this guide uses half the
            processor and memory and a quarter of the storage, so nothing is charged. Oracle
            reduced that allowance in June 2026, so older guides quoting 4 cores and 24 GB are
            out of date.
        </li>
        <li>
            A domain name. Nocturne gives every site its own subdomain, so the DNS provider
            must support wildcard records. If you do not have a domain, a free one from deSEC
            works well; see below.
        </li>
        <li>
            A decision about how to keep the server from being reclaimed as idle. It changes
            the command you run, so make it before you start.
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

    <h2 class="text-2xl font-bold mt-8 mb-4">Before you start: keeping it free</h2>
    <p class="text-muted-foreground mb-4">
        Oracle switches off Always Free servers on trial accounts that it judges idle for seven
        days in a row. Its measure is whether the processor, network and memory each stayed
        below 20 percent of capacity for nearly all of that week. A Nocturne server for one
        person does very little work by that yardstick, so left alone it is likely to be
        stopped after its first quiet week. There are two ways to prevent that, and one of them
        changes the command in Step 2, so pick now rather than later.
    </p>
    <Callout type="tip" title="Which one should you pick?">
        Pay As You Go suits most people: it is a single change in the Oracle console, adds
        nothing to the server, and paying accounts are served first when Ampere capacity is
        scarce. The trade-off is that the card already on file <em>can</em> be charged if
        anything outside the free allowance is ever created. Choose Folding@home instead if you
        would rather the account stay a trial that cannot be billed at all, and you do not mind
        a second piece of software with network access on the server that holds your health
        data.
    </Callout>
    <details class="mb-3">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">
            Option 1: upgrade the account to Pay As You Go (recommended)
        </summary>
        <p class="text-sm text-muted-foreground mt-2 mb-2">
            In the Oracle console, open <strong>Billing &amp; Cost Management</strong>, then
            <strong>Upgrade and Manage Payment</strong>, and choose Pay As You Go. Your card is
            already on file from sign-up. Always Free resources stay free after the upgrade, and
            Oracle's own terms exempt Pay As You Go accounts from idle reclamation. Everything
            this installer creates is within the free allowance, so the upgrade does not by
            itself cost anything.
        </p>
        <p class="text-sm text-muted-foreground mb-2">
            What changes is that the card can now be charged if something outside the free tier
            is ever created. The installer closes that off from both ends: it refuses a server
            size above the free allowance, and it writes an Oracle quota policy that stops the
            account creating anything outside the Always Free compute shapes at all, whether or
            not you have upgraded. That policy is a real limit rather than a warning: Oracle
            refuses the request instead of billing you for it. On top of that it sets a spending alert that emails you the
            moment the account is billed one unit of your currency. Nothing needs adding to the
            command in Step 2.
        </p>
    </details>
    <details class="mb-8">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">
            Option 2: stay on the trial and donate spare time to Folding@home
        </summary>
        <p class="text-sm text-muted-foreground mt-2 mb-2">
            <a href="https://foldingathome.org" class="text-primary hover:underline">Folding@home</a>
            is a research project that simulates how proteins fold, to help understand diseases
            such as Alzheimer's, cancer and, yes, diabetes. With this option the installer adds
            its client to the server and runs it from 02:00 to 04:00 UTC each night. Two hours a
            day of real computation is well above Oracle's idle threshold, so the server is never
            a candidate for reclamation, and the time goes to something useful.
        </p>
        <p class="text-sm text-muted-foreground mb-2">
            The client runs at the lowest priority the system has, so Nocturne and its alarms
            always come first. To choose this option, add
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">FOLDING=1</code>
            to the command in Step 2:
        </p>
        <CodeBlock code={"FOLDING=1 " + runCommand} class="mb-2" />
        <p class="text-sm text-muted-foreground mb-2">
            Work is contributed anonymously by default. To count it towards a team, add
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">FOLDING_TEAM=&lt;number&gt;</code>
            as well. Trial accounts cannot be charged, but the installer still creates the
            spending alert in case the account is upgraded later.
        </p>
    </details>

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
        subdomain under it, so pick something you are happy to keep. If you chose Folding@home
        above, add <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">FOLDING=1</code> in
        front.
    </p>
    <CodeBlock code={runCommand} class="mb-4" />
    <Callout type="tip" title="Keep the run alive if Cloud Shell disconnects">
        <p>
            Cloud Shell closes idle sessions, which stops the installer part-way. Run
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">tmux new -s nocturne</code>
            first and the run survives a dropped connection. Detach with Ctrl-B then D, and come
            back to it with
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">tmux attach -t nocturne</code>.
        </p>
    </Callout>
    <p class="text-muted-foreground mb-4">
        Early on it asks for a deSEC token. Paste one and the DNS records are created for you;
        press Enter instead and it prints two records for you to create by hand. Either way it
        keeps working while the server boots, and waits for the records before it requests
        certificates.
    </p>
    <details class="mb-4">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">What the script does</summary>
        <ul class="list-disc list-inside space-y-1 text-sm text-muted-foreground mt-2 mb-2">
            <li>Sets up a spending alert that emails you if the account is ever charged, and a quota policy that stops the account creating anything outside the Always Free compute shapes</li>
            <li>Creates a virtual network with ports 80 and 443 open, in your home region</li>
            <li>Reserves a public IP address so it never changes</li>
            <li>Generates an SSH key in your Cloud Shell home if you do not have one</li>
            <li>Starts an Ampere A1 server with 1 core and 6 GB of memory, half the Always Free allowance, retrying when Oracle has no free capacity</li>
            <li>Installs Folding@home on a nightly schedule if you asked for it above</li>
            <li>On the server: opens the firewall, installs Docker, downloads the Nocturne release bundle, generates the database passwords and starts everything once DNS resolves, checking each of these again on every run</li>
        </ul>
        <CodeBlock code={installScript} class="mt-2" maxHeight="400px" />
    </details>
    <Callout type="warning" title="If Oracle has no free capacity">
        <p class="mb-2">
            Ampere A1 servers are popular and regions often run out. The installer tries every
            availability domain in your region and keeps retrying for 30 minutes. It already
            asks for the smallest size the installer offers, 1 core and 6 GB, so there is
            nothing smaller to fall back to. If it gives up, run the command again
            later: it remembers your domain, so the command no longer needs it, and everything
            already created is reused. Upgrading to Pay As You Go also tends to end these
            errors, since paying accounts are served first when servers are scarce.
        </p>
        <p class="mb-2">To have it keep trying for longer instead of re-running by hand:</p>
        <CodeBlock code={"CAPACITY_RETRY_MINUTES=180 " + runCommand} />
    </Callout>
    <Callout type="info" title="If anything goes wrong, run it again">
        <p>
            Running the installer a second time is the fix for almost any failure. It looks every
            resource up before creating it, and checks the server itself on every run, so a run
            that was interrupted or stopped part-way carries on from where it got to rather than
            starting over. It remembers your domain, so the command on its own is enough.
        </p>
    </Callout>

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

    <h2 class="text-2xl font-bold mt-8 mb-4">Afterwards</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>
            The script ends by printing your <strong>instance key</strong>. It is the master
            credential for this installation, used for administrative API access and account
            recovery. Save it in a password manager. Running the script again shows it again.
        </li>
        <li>
            Download the SSH key the script created. It lives only in your Cloud Shell home,
            which Oracle deletes after six months without a Cloud Shell session. Use the Cloud
            Shell menu, <strong>Download</strong>, and enter
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.ssh/nocturne_oci</code>.
            Nocturne keeps running without it, but it is your only way onto the server.
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
