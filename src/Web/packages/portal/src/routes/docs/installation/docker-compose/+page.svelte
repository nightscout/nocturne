<script lang="ts">
    import SystemRequirements from "$lib/components/docs/SystemRequirements.svelte";
    import VerificationSteps from "$lib/components/docs/VerificationSteps.svelte";
    import NextSteps from "$lib/components/docs/NextSteps.svelte";
    import PasswordGenerator from "$lib/components/docs/PasswordGenerator.svelte";
    import envExample from "$lib/release/.env.example?raw";
</script>

<div class="max-w-3xl">
    <h1 class="text-4xl font-bold tracking-tight mb-4">Docker Compose</h1>
    <p class="text-lg text-muted-foreground mb-8">
        Deploy Nocturne on any server with Docker Compose from the command line.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Prerequisites</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>A Linux server, VPS, or Raspberry Pi with SSH access</li>
        <li>Docker Engine 24+ and Docker Compose 2.23.1+ installed</li>
        <li>A domain name (recommended) or static IP address</li>
    </ul>

    <SystemRequirements />

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 1: Download the release bundle</h2>
    <p class="text-muted-foreground mb-4">
        Download <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.yaml</code>
        and <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.env.example</code> from the
        <a href="https://github.com/nightscout/nocturne/releases/latest" class="text-primary hover:underline">
            latest GitHub Release
        </a>.
    </p>
    <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto mb-8"><code>mkdir nocturne && cd nocturne
# Download both files from the release page, then:
cp .env.example .env</code></pre>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 2: Configure environment variables</h2>
    <p class="text-muted-foreground mb-4">
        Edit <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.env</code> and fill in your
        values. Required fields are left blank; optional bot integrations are commented out.
        Use the generator below for each password field and <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">INSTANCE_KEY</code>.
    </p>
    <PasswordGenerator label="password" />
    <pre class="p-4 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto max-h-[400px] mb-8"><code>{envExample}</code></pre>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 3: Start the services</h2>
    <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto mb-4"><code>docker compose up -d</code></pre>
    <p class="text-muted-foreground mb-8">
        Docker will pull the images and start all services. First run takes a few minutes.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 4: Verify the installation</h2>
    <VerificationSteps />

    <h2 class="text-2xl font-bold mt-8 mb-4">Updating</h2>
    <p class="text-muted-foreground mb-4">
        Watchtower checks for image updates daily. To update manually:
    </p>
    <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto mb-8"><code>docker compose pull && docker compose up -d</code></pre>

    <h2 class="text-2xl font-bold mt-8 mb-4">Troubleshooting</h2>
    <p class="text-muted-foreground mb-4">Check the logs for error details:</p>
    <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto mb-4"><code># View all service logs
docker compose logs

# View logs for a specific service
docker compose logs nocturne-api

# Follow logs in real-time
docker compose logs -f</code></pre>
    <p class="text-muted-foreground mb-8">To start fresh, stop all services and remove volumes:</p>
    <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto mb-8"><code>docker compose down -v</code></pre>

    <h2 class="text-2xl font-bold mt-8 mb-4">Next Steps</h2>
    <NextSteps />
</div>
