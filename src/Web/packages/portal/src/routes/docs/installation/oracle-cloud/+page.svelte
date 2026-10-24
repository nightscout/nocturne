<script lang="ts">
    import Callout from "@nocturne/cms/components/Callout.svelte";
    import NextSteps from "$lib/components/docs/NextSteps.svelte";
    import SupportNocturne from "$lib/components/docs/SupportNocturne.svelte";
    import CodeBlock from "$lib/components/docs/CodeBlock.svelte";
    import installScript from "$lib/release/oracle-cloud/oracle-cloud-install.sh?raw";

    const runCommand =
        "BASE_DOMAIN=nocturne.example.com bash <(curl -fsSL https://github.com/nightscout/nocturne/releases/latest/download/oracle-cloud-install.sh)";
</script>

<div class="max-w-3xl">
    <h1 class="text-4xl font-bold tracking-tight mb-4">Oracle Cloud</h1>
    <p class="text-lg text-muted-foreground mb-8">
        Run Nocturne on Oracle Cloud's Always Free tier. One command in the browser sets
        up the server, the network, HTTPS and Nocturne itself. No SSH or Docker knowledge
        needed.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">What you need</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>
            An <a href="https://www.oracle.com/cloud/free/" class="text-primary hover:underline">Oracle Cloud account</a>.
            Sign-up requires a payment card to verify your identity. The region you pick at sign-up becomes your home region. The free server can only be created there, and the choice cannot be changed later, so pick the region closest to you. Always Free gives you 2 Ampere cores, 12 GB of memory and 200 GB of storage. Oracle halved the cores and memory in June 2026, so older guides that quote 4 cores and 24 GB are out of date. This guide uses half the cores and memory and a quarter of the storage, which stays well inside the free allowance.
        </li>
        <li>
            A domain name. Nocturne gives every site its own subdomain, so the DNS provider
            must support wildcard records. If you do not have a domain, a free one from DuckDNS
            works well: <a href="#duckdns" class="text-primary hover:underline">see below</a>.
        </li>
        <li>
            A decision about how to keep the server from being reclaimed as idle. It changes
            the command you run, so make it before you start.
        </li>
    </ul>

    <h2 id="duckdns" class="text-2xl font-bold mt-8 mb-4 scroll-mt-24">No domain yet? Get one from DuckDNS</h2>
    <p class="text-muted-foreground mb-4">
        <a href="https://www.duckdns.org" class="text-primary hover:underline">DuckDNS</a> is a
        free dynamic DNS service that gives out names under <strong>duckdns.org</strong>, each
        with a wildcard for the subdomains below it. The installer uses your DuckDNS token to
        point your name at your server, so there is nothing to set up by hand.
    </p>
    <ol class="list-decimal list-inside space-y-2 text-muted-foreground mb-8">
        <li>Sign in at duckdns.org with one of the accounts it offers, such as GitHub or Google.</li>
        <li>
            Under <strong>domains</strong>, type a name and click <strong>add domain</strong>.
            Your domain is then
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">yourname.duckdns.org</code>.
        </li>
        <li>
            Copy the <strong>token</strong> shown at the top of the page. It looks like
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">01234567-89ab-cdef-0123-456789abcdef</code>.
            It controls every name on your account, so keep it to yourself.
        </li>
    </ol>
    <p class="text-muted-foreground mb-8">
        For a domain you already own, you add two records by hand at your DNS provider in
        <a href="#step-3" class="text-primary hover:underline">Step 3</a>.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Before you start: keeping it free</h2>
    <p class="text-muted-foreground mb-4">
        Oracle switches off servers on Always Free accounts that it judges idle for seven
        days in a row. Its measure is whether the processor, network and memory each stayed
        below 20 percent of capacity for nearly all of that week. A Nocturne server for one
        person does very little work by that yardstick, so left alone it is likely to be
        stopped after its first quiet week. There are two ways to prevent that, and one of them
        changes the command in <a href="#step-2" class="text-primary hover:underline">Step 2</a>,
        so pick now rather than later.
    </p>
    <Callout type="tip" title="Which option to choose">
        Pay As You Go suits most people: it is a single change in the Oracle console, adds
        nothing to the server, and paying accounts are served first when Ampere capacity is
        scarce. The trade-off is that the card already on file <em>can</em> be charged if
        anything outside the free allowance is ever created. Choose Folding@home instead if you
        would rather keep an Always Free account that cannot be billed at all, and you do not
        mind a second piece of software with network access on the server that holds your
        health data.
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
            refuses the request instead of billing you for it. The installer also sets a spending
            alert that emails you the moment the account is billed one unit of your currency.
            Nothing needs adding to the command in
            <a href="#step-2" class="text-primary hover:underline">Step 2</a>.
        </p>
    </details>
    <details class="mb-8">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">
            Option 2: stay on an Always Free account and donate spare time to Folding@home
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
            to the command in <a href="#step-2" class="text-primary hover:underline">Step 2</a>:
        </p>
        <CodeBlock code={"FOLDING=1 " + runCommand} class="mb-2" />
        <p class="text-sm text-muted-foreground mb-2">
            Work is contributed anonymously by default. To count it towards a team, add
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">FOLDING_TEAM=&lt;number&gt;</code>
            as well. Always Free accounts cannot be charged, but the installer still creates the
            spending alert in case the account is upgraded later.
        </p>
    </details>

    <h2 id="step-1" data-install-step="1" class="text-2xl font-bold mt-8 mb-4 scroll-mt-24">Step 1: Open Cloud Shell</h2>
    <p class="text-muted-foreground mb-8">
        Sign in to the <a href="https://cloud.oracle.com" class="text-primary hover:underline">Oracle Cloud console</a>
        and select the terminal icon in the top-right toolbar, labeled <strong>Developer tools</strong>,
        then <strong>Cloud Shell</strong>. A terminal opens at the bottom of the page. It is already
        signed in as you, so there is nothing to install or configure.
    </p>

    <h2 id="step-2" data-install-step="2" class="text-2xl font-bold mt-8 mb-4 scroll-mt-24">Step 2: Run the installer</h2>
    <p class="text-muted-foreground mb-4">
        Paste this into Cloud Shell, replacing
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">nocturne.example.com</code>
        with your domain, such as
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">yourname.duckdns.org</code>.
        Nocturne will answer on that name and every site you create gets a
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
        If your domain is under duckdns.org, the installer asks early on for your DuckDNS token.
        <strong>Nothing appears on screen as you paste it</strong> – the token is hidden, and the
        installer tells you how many characters it read so you can see the paste landed. If
        DuckDNS does not accept it you can paste another. Press Enter with nothing pasted and it
        tells you the address to set yourself in
        <a href="#step-3" class="text-primary hover:underline">Step 3</a> instead. For any other
        domain, it prints the two records to create by hand. Either way it keeps working while the
        server boots, and waits for DNS before it requests certificates.
    </p>
    <details class="mb-4">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">What the script does</summary>
        <ul class="list-disc list-inside space-y-1 text-sm text-muted-foreground mt-2 mb-2">
            <li>Sets up a spending alert that emails you if the account is ever charged, and a quota policy that stops the account creating anything outside the Always Free compute shapes</li>
            <li>Creates a virtual network with ports 80 and 443 open, in your home region</li>
            <li>Reserves a public IP address so it never changes</li>
            <li>Points your DuckDNS name at that address when you give it the token. For any other domain, it prints the two records to create by hand</li>
            <li>Generates an SSH key in your Cloud Shell home if you do not have one</li>
            <li>Starts an Ampere A1 server with 1 core and 6 GB of memory, half the Always Free allowance, retrying when Oracle has no free capacity</li>
            <li>Installs Folding@home on a nightly schedule if you chose it above</li>
            <li>On the server: opens the firewall, installs Docker, downloads the Nocturne release bundle, generates the database passwords and starts everything once DNS resolves, checking each of these again on every run</li>
        </ul>
        <CodeBlock code={installScript} class="mt-2" maxHeight="400px" />
    </details>
    <Callout type="warning" title="If Oracle has no free capacity">
        <p class="mb-2">
            Ampere A1 servers are popular and regions often run out. The installer tries every
            availability domain in your region and keeps retrying for 30 minutes. It already
            requests the smallest size the installer offers, 1 core and 6 GB, so there is
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

    <h2 id="step-3" data-install-step="3" class="text-2xl font-bold mt-8 mb-4 scroll-mt-24">Step 3: Add the DNS records</h2>
    <p class="text-muted-foreground mb-4">
        Skip this step if you gave the installer a DuckDNS token. For a DuckDNS name without one,
        sign in at duckdns.org, put the IP address the script printed into the
        <strong>current ip</strong> box next to your name and click <strong>update ip</strong>.
        For any other domain, create two <strong>A</strong> records at your domain provider,
        pointing at that address:
    </p>
    <CodeBlock code={"nocturne.example.com      A   <the IP address>\n*.nocturne.example.com    A   <the IP address>"} class="mb-4" />
    <p class="text-muted-foreground mb-8">
        The second one is a wildcard. It makes every site's subdomain resolve without a record
        each. If you use Cloudflare, leave the proxy off for both records, shown as a gray cloud.
        Cloudflare's proxy cannot carry the certificates Nocturne issues for its own subdomains.
    </p>

    <h2 id="step-4" data-install-step="4" class="text-2xl font-bold mt-8 mb-4 scroll-mt-24">Step 4: Create your first site</h2>
    <p class="text-muted-foreground mb-8">
        Once the script reports that Nocturne is answering, open
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">https://your-domain</code>
        in a browser. You are taken to the setup page to name your first site and create the
        owner account, with a passkey or a sign-in provider. A passkey is your phone's or
        computer's built-in unlock. If you choose one, register a second device as soon as you
        can, because there is no password to fall back on.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Afterwards</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>
            The script ends by printing your <strong>instance key</strong>. It is the master
            credential for this installation, used for administrative API access and account
            recovery. Save it in a password manager. Running the script a second time prints it
            once more.
        </li>
        <li>
            Download the SSH key the script created. It lives only in your Cloud Shell home,
            which Oracle deletes after six months without a Cloud Shell session. Use the Cloud
            Shell menu, <strong>Download</strong>, and enter
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.ssh/nocturne_oci</code>.
            Nocturne keeps running without it, but it is your only way onto the server.
        </li>
        <li>Nocturne starts again by itself when the server reboots.</li>
        <li>
            To get a shell on the server, run the SSH command the script printed. Everything
            lives in <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">/opt/nocturne</code>.
        </li>
        <li>
            To update Caddy and Postgres, run
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">cd /opt/nocturne &amp;&amp; sudo docker compose pull &amp;&amp; sudo docker compose up -d</code>
            on the server.
        </li>
        <li>
            Nothing backs up your data automatically. In the Oracle console, under Block
            Storage, you can take a backup of the server's boot volume at any time. The free
            tier includes five volume backups, so take them by hand and delete old ones rather
            than attaching a scheduled policy, which would keep more than five and be charged.
        </li>
        <li>
            To remove everything, terminate the instance in the Oracle console, then delete the
            reserved public IP and the <strong>nocturne</strong> virtual cloud network.
        </li>
    </ul>

    <h2 data-install-step="next-steps" class="text-2xl font-bold mt-8 mb-4">Next Steps</h2>
    <NextSteps />

    <SupportNocturne />
</div>
