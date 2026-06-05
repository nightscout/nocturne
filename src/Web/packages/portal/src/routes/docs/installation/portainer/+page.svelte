<script lang="ts">
    import NextSteps from "$lib/components/docs/NextSteps.svelte";
    import PasswordGenerator from "$lib/components/docs/PasswordGenerator.svelte";
    import CodeBlock from "$lib/components/docs/CodeBlock.svelte";
    import { Info } from "@lucide/svelte";

    const templateUrl = "https://raw.githubusercontent.com/nightscout/nocturne/main/deploy/portainer/templates.json";
</script>

<div class="max-w-3xl">
    <div class="flex items-center gap-4 mb-4">
        <img
            src="/logos/portainer.jpg"
            alt="Portainer"
            class="w-12 h-12 rounded-lg object-cover shrink-0"
        />
        <h1 class="text-4xl font-bold tracking-tight">Portainer</h1>
    </div>
    <p class="text-lg text-muted-foreground mb-8">
        Deploy Nocturne using the Portainer web interface. No command-line access required.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Prerequisites</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>A running Portainer instance (Community or Business Edition)</li>
        <li>Docker Engine 24+ and Docker Compose 2.23.1+</li>
        <li>
            A domain with a DNS <strong>A</strong> record pointing at your server
            (<code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">example.com</code>),
            plus a wildcard <strong>A</strong> record
            (<code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">*.example.com</code>)
            to run more than one tenant.
        </li>
        <li>Ports <strong>80</strong> and <strong>443</strong> open to the internet for automatic TLS certificates.</li>
    </ul>

    <h2 class="text-2xl font-bold mt-8 mb-4">Option 1: App Template (recommended)</h2>
    <p class="text-muted-foreground mb-6">
        The Nocturne app template lets you deploy directly from Portainer's template gallery with
        a guided form for all configuration values.
    </p>

    <h3 class="text-xl font-semibold mt-6 mb-3">Step 1: Add the template URL</h3>
    <p class="text-muted-foreground mb-4">
        In Portainer, go to <strong class="text-foreground">Settings → App Templates</strong> and
        set the URL to:
    </p>
    <CodeBlock code={templateUrl} class="mb-4" />
    <p class="text-muted-foreground mb-8">
        Click <strong class="text-foreground">Save settings</strong>.
    </p>

    <h3 class="text-xl font-semibold mt-6 mb-3">Step 2: Deploy from the template</h3>
    <ol class="list-decimal list-inside space-y-3 text-muted-foreground mb-8">
        <li>
            Navigate to <strong class="text-foreground">App Templates</strong> in the sidebar.
        </li>
        <li>
            Find <strong class="text-foreground">Nocturne</strong> in the list and click it.
        </li>
        <li>
            Fill in the configuration form. Set a strong password for each PostgreSQL role and a
            unique <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">INSTANCE_KEY</code>
            (minimum 12 characters). Use the generator below for each secret field.
        </li>
        <li class="list-none -ml-5">
            <PasswordGenerator label="password" />
        </li>
        <li>
            Click <strong class="text-foreground">Deploy the stack</strong>.
        </li>
    </ol>

    <div class="p-4 rounded-lg border border-blue-500/30 bg-blue-500/5 mb-8 not-prose">
        <div class="flex items-start gap-3">
            <Info class="w-5 h-5 text-blue-500 mt-0.5 shrink-0" />
            <p class="text-sm text-muted-foreground">
                <strong class="text-blue-700 dark:text-blue-400">Bot integrations are optional.</strong>
                Leave Discord, Telegram, Slack, and WhatsApp fields blank if you don't need them.
                They can be configured later by updating the stack.
            </p>
        </div>
    </div>

    <h2 class="text-2xl font-bold mt-8 mb-4">Option 2: Manual deployment</h2>
    <p class="text-muted-foreground mb-4">
        If you prefer to configure the stack manually, download
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.yaml</code>
        and <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.env.example</code> from the
        <a href="https://github.com/nightscout/nocturne/releases/latest" class="text-primary hover:underline">
            latest GitHub Release
        </a>, then:
    </p>
    <ol class="list-decimal list-inside space-y-3 text-muted-foreground mb-8">
        <li>
            In Portainer, go to <strong class="text-foreground">Stacks → Add stack</strong>.
            Name it <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">nocturne</code>
            and select <strong class="text-foreground">Web editor</strong>.
        </li>
        <li>
            Paste the contents of <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.yaml</code>
            into the editor.
        </li>
        <li>
            Scroll to <strong class="text-foreground">Environment variables</strong>, click
            <strong class="text-foreground">Advanced mode</strong>, and paste your values from
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.env.example</code>
            in <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">KEY=VALUE</code> format.
        </li>
        <li>
            Click <strong class="text-foreground">Deploy the stack</strong>.
        </li>
    </ol>

    <h2 class="text-2xl font-bold mt-8 mb-4">Verify the installation</h2>
    <p class="text-muted-foreground mb-4">
        In Portainer, navigate to your <strong class="text-foreground">nocturne</strong> stack.
        All containers should show as <strong class="text-foreground">Running</strong>. Click any
        container to view its logs and check for errors.
    </p>
    <p class="text-muted-foreground mb-8">
        Once running, the bundled Caddy proxy obtains Let's Encrypt TLS certificates
        automatically and serves the stack at
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">https://your-domain</code>
        on ports 80 and 443 — no certificate files to manage. Each tenant subdomain
        gets its own certificate on demand the first time it is visited.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Updating</h2>
    <p class="text-muted-foreground mb-4">
        Watchtower is included and checks for image updates daily. To update manually in Portainer:
    </p>
    <ol class="list-decimal list-inside space-y-2 text-muted-foreground mb-8">
        <li>Navigate to your <strong class="text-foreground">nocturne</strong> stack</li>
        <li>Click <strong class="text-foreground">Editor</strong></li>
        <li>Click <strong class="text-foreground">Update the stack</strong></li>
        <li>Check <strong class="text-foreground">Re-pull image and redeploy</strong></li>
        <li>Click <strong class="text-foreground">Update</strong></li>
    </ol>

    <h2 class="text-2xl font-bold mt-8 mb-4">Connectors</h2>
    <p class="text-muted-foreground mb-8">
        Data source connectors (Dexcom, LibreLinkUp, Glooko, etc.) are configured through the
        Nocturne UI after deployment — no compose changes required.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Next Steps</h2>
    <NextSteps />
</div>
