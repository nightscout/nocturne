<script lang="ts">
    import SystemRequirements from "$lib/components/docs/SystemRequirements.svelte";
    import VerificationSteps from "$lib/components/docs/VerificationSteps.svelte";
    import NextSteps from "$lib/components/docs/NextSteps.svelte";
    import PasswordGenerator from "$lib/components/docs/PasswordGenerator.svelte";
    import CodeBlock from "$lib/components/docs/CodeBlock.svelte";
    import envExample from "$lib/release/docker-compose/.env.example?raw";
    import dockerCompose from "$lib/release/docker-compose/docker-compose.yaml?raw";
</script>

<div class="max-w-3xl">
    <div class="flex items-center gap-4 mb-4">
        <img
            src="/logos/docker-compose.png"
            alt="Docker Compose"
            class="w-12 h-12 object-contain shrink-0"
        />
        <h1 class="text-4xl font-bold tracking-tight">Docker Compose</h1>
    </div>
    <p class="text-lg text-muted-foreground mb-8">
        Deploy Nocturne on any server with Docker Compose from the command line.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Prerequisites</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>A Linux server, VPS, or Raspberry Pi with SSH access</li>
        <li>Docker Engine 24+ and Docker Compose 2.23.1+ installed</li>
        <li>
            A domain with a DNS <strong>A</strong> record pointing at your server
            (<code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">example.com</code>).
            To run more than one tenant, also add a wildcard
            <strong>A</strong> record
            (<code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">*.example.com</code>)
            so each tenant subdomain resolves.
        </li>
        <li>Ports <strong>80</strong> and <strong>443</strong> open to the internet — the bundled proxy uses them to obtain and serve TLS certificates.</li>
    </ul>

    <SystemRequirements />

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 1: Download the release bundle</h2>
    <p class="text-muted-foreground mb-4">
        Download the <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose</code>
        bundle from the
        <a href="https://github.com/nightscout/nocturne/releases/latest" class="text-primary hover:underline">
            latest GitHub Release
        </a>. Keep the bundled
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">caddy/</code> directory
        next to <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.yaml</code> —
        the TLS proxy config is mounted from it. The bundle also ships a
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.byo-proxy.yaml</code>
        override for operators who run their own reverse proxy (see below).
    </p>
    <CodeBlock code={"mkdir nocturne && cd nocturne\n# Download and extract the docker-compose bundle from the release page, then:\ncp .env.example .env"} class="mb-4" />

    <details class="mb-8">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">View docker-compose.yaml</summary>
        <CodeBlock code={dockerCompose} class="mt-2" maxHeight="400px" />
    </details>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 2: Configure environment variables</h2>
    <p class="text-muted-foreground mb-4">
        Edit <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.env</code> and fill in your
        values. Required fields are left blank; optional bot integrations are commented out.
        Use the generator below for each password field and <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">INSTANCE_KEY</code>.
    </p>
    <PasswordGenerator label="password" />
    <CodeBlock code={envExample} class="mb-8" maxHeight="400px" />

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 3: Start the services</h2>
    <CodeBlock code="docker compose up -d" class="mb-4" />
    <p class="text-muted-foreground mb-8">
        Docker will pull the images and start all services. First run takes a few minutes.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">HTTPS is automatic</h2>
    <p class="text-muted-foreground mb-4">
        The bundled Caddy reverse proxy obtains and renews Let's Encrypt TLS
        certificates automatically — no API keys or certificate files to manage.
        Set <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">BASE_DOMAIN</code>,
        point your DNS at the server, and open ports 80 and 443. The apex domain
        is issued a certificate on first start, and each tenant subdomain gets one
        on demand the first time it is visited.
    </p>
    <p class="text-muted-foreground mb-4">
        Already run your own reverse proxy (nginx, Traefik, Caddy)? Use the
        bring-your-own-proxy override to disable the bundled Caddy and expose the
        gateway on plain HTTP port 8080 for your proxy to forward to:
    </p>
    <CodeBlock code={"docker compose -f docker-compose.yaml -f docker-compose.byo-proxy.yaml up -d"} class="mb-8" />

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 4: Verify the installation</h2>
    <VerificationSteps />

    <h2 class="text-2xl font-bold mt-8 mb-4">Updating</h2>
    <p class="text-muted-foreground mb-4">
        Watchtower checks for image updates daily. To update manually:
    </p>
    <CodeBlock code="docker compose pull && docker compose up -d" class="mb-8" />

    <h2 class="text-2xl font-bold mt-8 mb-4">Troubleshooting</h2>
    <p class="text-muted-foreground mb-4">Check the logs for error details:</p>
    <CodeBlock code={"# View all service logs\ndocker compose logs\n\n# View logs for a specific service\ndocker compose logs nocturne-api\n\n# Follow logs in real-time\ndocker compose logs -f"} class="mb-4" />
    <p class="text-muted-foreground mb-8">To start fresh, stop all services and remove volumes:</p>
    <CodeBlock code="docker compose down -v" class="mb-8" />

    <h2 class="text-2xl font-bold mt-8 mb-4">Next Steps</h2>
    <NextSteps />
</div>
