<script lang="ts">
    import { ArtworkHero } from "water-ui";
    import { resolve } from "$app/paths";
    import NextSteps from "#lib/components/docs/NextSteps.svelte";
    import SupportNocturne from "#lib/components/docs/SupportNocturne.svelte";
    import EnvFileGenerator from "#lib/components/docs/EnvFileGenerator.svelte";
    import CodeBlock from "#lib/components/docs/CodeBlock.svelte";
    import InstallGuideBar from "#lib/components/docs/install/InstallGuideBar.svelte";
    import StepHeading from "#lib/components/docs/install/StepHeading.svelte";
    import RouteChoice from "#lib/components/docs/install/RouteChoice.svelte";
    import Domain from "#lib/components/docs/install/Domain.svelte";
    import SiteLink from "#lib/components/docs/install/SiteLink.svelte";
    import KeyFields from "#lib/components/docs/install/KeyFields.svelte";
    import CopyKeysFile from "#lib/components/docs/install/CopyKeysFile.svelte";
    import { createInstallGuide } from "#lib/components/docs/install/guide.svelte.js";
    import { DOCS_HERO } from "#lib/data/docs-artwork.js";
    import envExample from "#lib/release/portainer/default.env.example?raw";
    import stackFile from "#lib/release/portainer/docker-compose.yaml?raw";

    const hero = DOCS_HERO["installation/portainer"];

    const templateUrl = "https://raw.githubusercontent.com/nightscout/nocturne/main/deploy/portainer/templates.json";

    const guide = createInstallGuide({ id: "portainer", envTemplate: envExample });
    const route = $derived(guide.choice("deploy", "template"));

    const STEPS = [
        { id: "keys", title: "Make your keys and passwords" },
        { id: "deploy", title: "Deploy in Portainer" },
        { id: "verify", title: "Open your site" },
    ];

    const ROUTES = [
        {
            value: "template",
            title: "App template",
            description: "Portainer shows a form. You paste one value into each field.",
            recommended: true,
        },
        {
            value: "editor",
            title: "Stack editor",
            description: "You paste the stack file and the keys file into Portainer.",
        },
    ];

    // The labels of the template's form, as Portainer shows them.
    const TEMPLATE_FIELDS = [
        { label: "Base domain", name: "BASE_DOMAIN" },
        { label: "Instance key", name: "INSTANCE_KEY" },
        { label: "PostgreSQL bootstrap password", name: "POSTGRES_PASSWORD" },
        { label: "Migrator role password", name: "POSTGRES_MIGRATOR_PASSWORD" },
        { label: "App role password", name: "POSTGRES_APP_PASSWORD" },
        { label: "Web role password", name: "POSTGRES_WEB_PASSWORD" },
    ];

    // @wc-ignore
    const SUMMARY = "text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground";
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

    <h2 class="text-2xl font-bold mt-8 mb-4">Before you start</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>A running Portainer instance (Community or Business Edition)</li>
        <li>Docker Engine 24+ and Docker Compose 2.23.1+</li>
        <li>
            A domain with a DNS <strong>A</strong> record for <Domain /> that points at your
            server. To run more than one tenant, add a wildcard <strong>A</strong> record for
            <Domain prefix="*." /> also.
        </li>
        <li>Ports <strong>80</strong> and <strong>443</strong> open to the internet for automatic TLS certificates.</li>
    </ul>

    <InstallGuideBar steps={STEPS} />

    <StepHeading id="keys">Step 1: Make your keys and passwords</StepHeading>
    <p class="text-muted-foreground mb-4">
        This page made a file in your browser, with a new random value for each key and
        password. The values do not leave your browser. Download the file and keep a copy.
        Step 2 uses these values.
    </p>
    <EnvFileGenerator />

    <StepHeading id="deploy">Step 2: Deploy in Portainer</StepHeading>
    <p class="text-muted-foreground mb-4">
        There are two procedures. Select one. This page then shows only that procedure.
    </p>
    <RouteChoice id="deploy" label="How to deploy in Portainer" options={ROUTES} fallback="template" />

    {#if route === "template"}
        <h3 data-install-step="1" class="text-xl font-semibold mt-6 mb-3">Add the template URL</h3>
        <p class="text-muted-foreground mb-4">
            In Portainer, go to <strong class="text-foreground">Settings &rsaquo; App Templates</strong> and
            set the URL to:
        </p>
        <CodeBlock code={templateUrl} class="mb-4" />
        <p class="text-muted-foreground mb-8">
            Select <strong class="text-foreground">Save settings</strong>.
        </p>

        <h3 data-install-step="2" class="text-xl font-semibold mt-6 mb-3">Deploy from the template</h3>
        <ol class="list-decimal list-inside space-y-3 text-muted-foreground mb-8">
            <li>
                Navigate to <strong class="text-foreground">App Templates</strong> in the sidebar.
            </li>
            <li>
                Find <strong class="text-foreground">Nocturne</strong> in the list and select it.
            </li>
            <li>
                Fill in the configuration form. Copy each value below into the field with the same
                name.
                <KeyFields fields={TEMPLATE_FIELDS} keysStep="keys" />
            </li>
            <li>
                Leave the other fields as they are. If you do not need Discord, Telegram, Slack,
                or WhatsApp, leave their fields blank. You can configure them later.
            </li>
            <li>
                Select <strong class="text-foreground">Deploy the stack</strong>.
            </li>
        </ol>
    {:else}
        <ol data-install-step="manual" class="list-decimal list-inside space-y-3 text-muted-foreground mb-8">
            <li>
                In Portainer, go to <strong class="text-foreground">Stacks &rsaquo; Add stack</strong>.
                Name it <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">nocturne</code>
                and select <strong class="text-foreground">Web editor</strong>.
            </li>
            <li>
                Paste the contents of <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.yaml</code>
                into the editor. The file is below. It is also in the
                <a href="https://github.com/nightscout/nocturne/releases/latest" class="text-primary hover:underline">latest GitHub Release</a>.
                <details class="mt-3">
                    <summary class={SUMMARY}>Show docker-compose.yaml</summary>
                    <CodeBlock code={stackFile} class="mt-2" maxHeight="400px" />
                </details>
            </li>
            <li>
                Scroll to <strong class="text-foreground">Environment variables</strong>. Select
                <strong class="text-foreground">Advanced mode</strong>. Paste the file that you
                made in Step 1.
                <div class="mt-3">
                    <CopyKeysFile keysStep="keys" />
                </div>
            </li>
            <li>
                Select <strong class="text-foreground">Deploy the stack</strong>.
            </li>
        </ol>
    {/if}

    <StepHeading id="verify" installStep="verify">Step 3: Open your site</StepHeading>
    <p class="text-muted-foreground mb-4">
        In Portainer, go to your <strong class="text-foreground">nocturne</strong> stack.
        Make sure that all containers show as <strong class="text-foreground">Running</strong>. Select any
        container to view its logs and check for errors.
    </p>
    <p class="text-muted-foreground mb-8">
        Then open <SiteLink />. The bundled Caddy proxy obtains Let's Encrypt TLS certificates
        automatically, with no certificate files to manage. Each tenant subdomain gets its own
        certificate on demand the first time it is visited.
    </p>

    <h2 data-install-step="next-steps" class="text-2xl font-bold mt-12 mb-4">Next steps</h2>
    <NextSteps />

    <h2 class="text-2xl font-bold mt-12 mb-4">After the installation</h2>
    <details class="mb-3">
        <summary class={SUMMARY}>Update Nocturne</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Watchtower is included and checks for image updates daily, except for Caddy and Postgres. To update everything, including Caddy and Postgres, in Portainer:
        </p>
        <ol class="list-decimal list-inside space-y-2 text-muted-foreground mb-4">
            <li>Navigate to your <strong class="text-foreground">nocturne</strong> stack</li>
            <li>Select <strong class="text-foreground">Editor</strong></li>
            <li>Select <strong class="text-foreground">Update the stack</strong></li>
            <li>Check <strong class="text-foreground">Re-pull image and redeploy</strong></li>
            <li>Select <strong class="text-foreground">Update</strong></li>
        </ol>
    </details>
    <details class="mb-3">
        <summary class={SUMMARY}>Restarts and logs</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            The stack uses the same restart policy and log rotation as the
            <a href={resolve("/docs/installation/docker-compose")} class="text-primary hover:underline">Docker Compose bundle</a>.
            Portainer stacks do not read override files. To keep your own logging driver,
            edit the <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">logging</code>
            section of each service in the stack editor instead.
        </p>
    </details>
    <details class="mb-3">
        <summary class={SUMMARY}>Connectors</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Data source connectors (Dexcom, LibreLinkUp, Glooko, and others) are configured through the
            Nocturne UI after deployment. No compose changes are required.
        </p>
    </details>
    <details class="mb-8">
        <summary class={SUMMARY}>PostgreSQL host-directory storage</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Portainer's stack editor and app template use the default named Docker volume.
            Portainer does not apply Compose override files. To put PostgreSQL data in a host
            directory, deploy the Docker Compose bundle from the command line with both
            <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">docker-compose.yaml</code>
            and <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">docker-compose.bind-data.yaml</code>.
            See the <a href={resolve("/docs/installation/docker-compose")} class="text-primary hover:underline">Docker Compose guide</a>
            for setup and existing-database migration precautions.
        </p>
    </details>

    <SupportNocturne />
</div>
