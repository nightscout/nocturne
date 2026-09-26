<script lang="ts">
    import { ArtworkHero } from "@nocturne/watercolour";
    import { DOCS_HERO } from "$lib/data/docs-artwork";
    import { resolve } from "$app/paths";
    import SystemRequirements from "$lib/components/docs/SystemRequirements.svelte";
    import VerificationSteps from "$lib/components/docs/VerificationSteps.svelte";
    import NextSteps from "$lib/components/docs/NextSteps.svelte";
    import SupportNocturne from "$lib/components/docs/SupportNocturne.svelte";
    import PasswordGenerator from "$lib/components/docs/PasswordGenerator.svelte";
    import CodeBlock from "$lib/components/docs/CodeBlock.svelte";
    import envExample from "$lib/release/docker-compose/default.env.example?raw";
    import dockerCompose from "$lib/release/docker-compose/docker-compose.yaml?raw";

    const hero = DOCS_HERO["installation/docker-compose"];
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
    <h1 class="text-4xl font-bold tracking-tight mb-4">Docker Compose</h1>
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
        <li>Ports <strong>80</strong> and <strong>443</strong> open to the internet. The bundled proxy uses them to obtain and serve TLS certificates.</li>
    </ul>

    <SystemRequirements />

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 1: Download the release bundle</h2>
    <p class="text-muted-foreground mb-4">
        Download the <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose</code>
        bundle from the
        <a href="https://github.com/nightscout/nocturne/releases/latest" class="text-primary hover:underline">
            latest GitHub Release
        </a>. The bundle is self-contained: just
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.yaml</code>
        and the environment template, which GitHub lists as
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">default.env.example</code>.
        The database init script and the TLS proxy config are embedded directly in
        the compose file, so there are no extra directories to keep alongside it. The
        bundle also ships a
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.byo-proxy.yaml</code>
        override for operators who run their own reverse proxy (see below).
    </p>
    <CodeBlock code={"mkdir nocturne && cd nocturne\ncurl -LO https://github.com/nightscout/nocturne/releases/latest/download/docker-compose.yaml\ncurl -L -o .env https://github.com/nightscout/nocturne/releases/latest/download/default.env.example"} class="mb-4" />

    <details class="mb-8">
        <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">View docker-compose.yaml</summary>
        <CodeBlock code={dockerCompose} class="mt-2" maxHeight="400px" />
    </details>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 2: Configure environment variables</h2>
    <p class="text-muted-foreground mb-4">
        Edit <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">.env</code> and fill in your
        values. The required fields come first and are left blank; optional bot integrations are commented out.
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
        certificates automatically, with no API keys or certificate files to manage.
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
    <CodeBlock code="docker compose -f docker-compose.yaml -f docker-compose.byo-proxy.yaml up -d" class="mb-4" />
    <p class="text-muted-foreground mb-8">
        Your proxy must forward the original <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">Host</code>
        along with <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">X-Forwarded-Proto</code> and
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">X-Forwarded-Host</code>, or logins will
        fail with a 403. See
        <a href={resolve("/docs/installation/reverse-proxy")} class="text-primary hover:underline">Bring your own reverse proxy</a>
        for worked nginx, Traefik, and Caddy configurations.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Step 4: Verify the installation</h2>
    <VerificationSteps />

    <h2 class="text-2xl font-bold mt-8 mb-4">Updating</h2>
    <p class="text-muted-foreground mb-4">
        Watchtower checks for image updates daily. To update manually:
    </p>
    <CodeBlock code="docker compose pull && docker compose up -d" class="mb-4" />
    <p class="text-muted-foreground mb-8">
        Watchtower updates images, not the compose file. If you installed before the
        restart policy and log rotation below were added, download the new bundle and
        run <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker compose up -d</code>
        to pick them up. That first run recreates the containers, which clears their
        old logs; your database data is kept.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Restarts and logs</h2>
    <p class="text-muted-foreground mb-4">
        Every service sets <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">restart: unless-stopped</code>,
        so Docker restarts it when it exits or when the Docker daemon restarts, unless you
        stopped it. Docker's default policy is <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">no</code>
        (<a href="https://docs.docker.com/engine/containers/start-containers-automatically/" class="text-primary hover:underline">Docker docs</a>).
    </p>
    <p class="text-muted-foreground mb-8">
        Every service also logs to the <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">json-file</code>
        driver, rotated at three files of 10 MB. Left to its defaults, that driver never
        rotates, because <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">max-size</code>
        defaults to unlimited
        (<a href="https://docs.docker.com/engine/logging/drivers/json-file/" class="text-primary hover:underline">Docker docs</a>).
        A logging driver set on a container replaces the daemon's default
        (<a href="https://docs.docker.com/engine/logging/configure/" class="text-primary hover:underline">Docker docs</a>),
        so the bundle overrides any <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">log-driver</code>
        in your <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">daemon.json</code>.
        To keep yours, set <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">logging</code>
        on the services in a <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">docker-compose.override.yaml</code>.
        Compose reads that file only when you pass no <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">-f</code>
        (<a href="https://docs.docker.com/compose/how-tos/multiple-compose-files/merge/" class="text-primary hover:underline">Docker docs</a>),
        so with the bring-your-own-proxy command, also pass
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">-f docker-compose.override.yaml</code>.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Troubleshooting</h2>
    <p class="text-muted-foreground mb-4">Check the logs for error details:</p>
    <CodeBlock code={"# View all service logs\ndocker compose logs\n\n# View logs for a specific service\ndocker compose logs nocturne-api\n\n# Follow logs in real-time\ndocker compose logs -f"} class="mb-4" />
    <p class="text-muted-foreground mb-8">To start fresh, stop all services and remove volumes:</p>
    <CodeBlock code="docker compose down -v" class="mb-8" />

    <h2 class="text-2xl font-bold mt-8 mb-4">Next Steps</h2>
    <NextSteps />

    <SupportNocturne />
</div>
