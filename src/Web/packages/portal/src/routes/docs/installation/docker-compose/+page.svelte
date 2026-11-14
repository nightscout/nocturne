<script lang="ts">
    import { ArtworkHero } from "water-ui";
    import { DOCS_HERO } from "#lib/data/docs-artwork.js";
    import { resolve } from "$app/paths";
    import SystemRequirements from "#lib/components/docs/SystemRequirements.svelte";
    import VerificationSteps from "#lib/components/docs/VerificationSteps.svelte";
    import NextSteps from "#lib/components/docs/NextSteps.svelte";
    import SupportNocturne from "#lib/components/docs/SupportNocturne.svelte";
    import EnvFileGenerator from "#lib/components/docs/EnvFileGenerator.svelte";
    import CodeBlock from "#lib/components/docs/CodeBlock.svelte";
    import InstallGuideBar from "#lib/components/docs/install/InstallGuideBar.svelte";
    import StepHeading from "#lib/components/docs/install/StepHeading.svelte";
    import RouteChoice from "#lib/components/docs/install/RouteChoice.svelte";
    import Domain from "#lib/components/docs/install/Domain.svelte";
    import CopyKeysFile from "#lib/components/docs/install/CopyKeysFile.svelte";
    import { createInstallGuide } from "#lib/components/docs/install/guide.svelte.js";
    import Callout from "@nocturne/cms/components/Callout.svelte";
    import envExample from "#lib/release/docker-compose/default.env.example?raw";
    import dockerCompose from "#lib/release/docker-compose/docker-compose.yaml?raw";
    import bindDataCompose from "#lib/release/docker-compose/docker-compose.bind-data.yaml?raw";

    const hero = DOCS_HERO["installation/docker-compose"];

    const guide = createInstallGuide({ id: "docker-compose", envTemplate: envExample });
    const ownProxy = $derived(guide.choice("proxy", "bundled") === "own");
    const hostData = $derived(guide.choice("storage", "volume") === "host");

    const STEPS = [
        { id: "download", title: "Download the release bundle" },
        { id: "configure", title: "Make your keys and passwords" },
        { id: "start", title: "Start the services" },
        { id: "verify", title: "Verify the installation" },
    ];

    const PROXY_ROUTES = [
        {
            value: "bundled",
            title: "The bundled proxy",
            description: "Caddy gets and renews the TLS certificates. Ports 80 and 443 must be open.",
            recommended: true,
        },
        {
            value: "own",
            title: "My own reverse proxy",
            description: "nginx, Traefik, or a different proxy that you already operate.",
        },
    ];

    const STORAGE_ROUTES = [
        {
            value: "volume",
            title: "A Docker volume",
            description: "Docker manages the storage. There is nothing to configure.",
            recommended: true,
        },
        {
            value: "host",
            title: "A host directory",
            description: "PostgreSQL keeps its data in a directory that you select on the Docker host.",
        },
    ];

    // File names and commands, not user-facing text.
    // @wc-ignore
    const BASE_FILE = "docker-compose.yaml";
    // @wc-ignore
    const BIND_FILE = "docker-compose.bind-data.yaml";
    // @wc-ignore
    const PROXY_FILE = "docker-compose.byo-proxy.yaml";
    // @wc-ignore
    const RELEASE = "https://github.com/nightscout/nocturne/releases/latest/download/";

    /** `docker compose` with a `-f` for each file, or with none when the base file is the only one. */
    function composeWith(files: string[]): string {
        return files.length === 1
            ? "docker compose"
            : `docker compose ${files.map((file) => `-f ${file}`).join(" ")}`;
    }

    const files = $derived([BASE_FILE, ...(hostData ? [BIND_FILE] : []), ...(ownProxy ? [PROXY_FILE] : [])]);
    const overrides = $derived(files.length > 1);
    const compose = $derived(composeWith(files));
    // The stack as it was before the bind-data override, for a rollback to the named volume.
    const composeOnVolume = $derived(composeWith([BASE_FILE, ...(ownProxy ? [PROXY_FILE] : [])]));

    const downloadCommand = $derived(
        ["mkdir nocturne && cd nocturne", ...files.map((file) => `curl -LO ${RELEASE}${file}`)].join("\n"),
    );

    // @wc-ignore
    const upCommand = $derived(`${compose} up -d`);
    // @wc-ignore
    const updateCommand = $derived(`${compose} pull\n${compose} up -d`);
    // @wc-ignore
    const stopCommand = $derived(
        `${composeOnVolume} stop\npostgres_container="$(${composeOnVolume} ps -aq nocturne-postgres-server)"\ntest -n "$postgres_container"`,
    );
    // @wc-ignore
    const rollbackCommand = $derived(`${compose} down\n${composeOnVolume} up -d`);
    // @wc-ignore
    const logsCommand = $derived(
        `# View all service logs\n${compose} logs\n\n# View logs for a specific service\n${compose} logs nocturne-api\n\n# Follow logs in real-time\n${compose} logs -f`,
    );
    // @wc-ignore
    const resetCommand = $derived(`${compose} down -v`);

    // @wc-ignore
    const allowLocalLogin = $derived(
        [
            `${compose} exec nocturne-postgres-server sh -c 'sed -i "1i local all all trust" "$PGDATA/pg_hba.conf"'`,
            `${compose} kill -s SIGHUP nocturne-postgres-server`,
        ].join("\n"),
    );

    // @wc-ignore
    const resetPasswords = $derived(
        [
            `${compose} exec nocturne-postgres-server psql -U nocturne -d nocturne \\`,
            "  -c \"ALTER ROLE nocturne PASSWORD '<POSTGRES_PASSWORD>'\" \\",
            "  -c \"ALTER ROLE nocturne_migrator PASSWORD '<POSTGRES_MIGRATOR_PASSWORD>'\" \\",
            "  -c \"ALTER ROLE nocturne_app PASSWORD '<POSTGRES_APP_PASSWORD>'\" \\",
            "  -c \"ALTER ROLE nocturne_web PASSWORD '<POSTGRES_WEB_PASSWORD>'\"",
        ].join("\n"),
    );

    // @wc-ignore
    const removeLocalLogin = $derived(
        [
            `${compose} exec nocturne-postgres-server sh -c 'sed -i "1d" "$PGDATA/pg_hba.conf"'`,
            `${compose} kill -s SIGHUP nocturne-postgres-server`,
            `${compose} up -d`,
        ].join("\n"),
    );

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
    <h1 class="text-4xl font-bold tracking-tight mb-4">Docker Compose</h1>
    <p class="text-lg text-muted-foreground mb-8">
        Deploy Nocturne on any server with Docker Compose from the command line.
    </p>
    <Callout type="tip" title="The install script does these steps for you">
        <p>
            On a Linux server with the bundled proxy and a Docker volume, the
            <a href={resolve("/docs/installation/install-script")} class="text-primary hover:underline">install script</a>
            does every step on this page. Use this page to do the steps by hand, to use your
            own reverse proxy, or to keep the database in a host directory. A server that the
            install script made has its files in
            <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">/opt/nocturne</code>.
            Do not run the install script on a server that you installed from this page.
        </p>
    </Callout>

    <h2 class="text-2xl font-bold mt-8 mb-4">Before you start</h2>
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-8">
        <li>A Linux server, VPS, or Raspberry Pi with SSH access</li>
        <li>Docker Engine 24+ and Docker Compose 2.23.1+ installed</li>
        <li>
            A domain with a DNS <strong>A</strong> record for <Domain /> that points at your
            server. To run more than one tenant, add a wildcard <strong>A</strong> record for
            <Domain prefix="*." /> also, so each tenant subdomain resolves.
        </li>
        {#if !ownProxy}
            <li>Ports <strong>80</strong> and <strong>443</strong> open to the internet. The bundled proxy uses them to obtain and serve TLS certificates.</li>
        {/if}
    </ul>

    <SystemRequirements />

    <h2 class="text-2xl font-bold mt-8 mb-4">Your setup</h2>
    <p class="text-muted-foreground mb-6">
        The recommended answers are selected. Change an answer only if it does not fit your
        server. This page then shows only the commands and steps for your answers.
    </p>
    <h3 class="text-base font-semibold mt-4 mb-3">What serves HTTPS?</h3>
    <RouteChoice id="proxy" label="What serves HTTPS?" options={PROXY_ROUTES} fallback="bundled" />
    <h3 class="text-base font-semibold mt-4 mb-3">Where does PostgreSQL keep its data?</h3>
    <RouteChoice id="storage" label="Where does PostgreSQL keep its data?" options={STORAGE_ROUTES} fallback="volume" />

    <InstallGuideBar steps={STEPS} />

    <StepHeading id="download" installStep="1">Step 1: Download the release bundle</StepHeading>
    <p class="text-muted-foreground mb-4">
        Run these commands on your server. They download your setup from the
        <a href="https://github.com/nightscout/nocturne/releases/latest" class="text-primary hover:underline">latest GitHub Release</a>.
        The database init script and the TLS proxy config are embedded directly in
        <code class={CODE}>docker-compose.yaml</code>. There are no extra directories to keep alongside it.
    </p>
    <CodeBlock code={downloadCommand} class="mb-4" />

    <details class="mb-8">
        <summary class={SUMMARY}>View docker-compose.yaml</summary>
        <CodeBlock code={dockerCompose} class="mt-2" maxHeight="400px" />
    </details>

    <StepHeading id="configure" installStep="2">Step 2: Make your keys and passwords</StepHeading>
    <p class="text-muted-foreground mb-4">
        This page made a file in your browser, with a new random value for each key and
        password. The values do not leave your browser. Download the file and keep a copy.
    </p>
    <EnvFileGenerator />
    <p class="text-muted-foreground mb-4">
        Then copy the file to <code class={CODE}>.env</code> in the
        <code class={CODE}>nocturne</code> folder on your server:
    </p>
    <CopyKeysFile keysStep="configure" />
    <CodeBlock code="nano .env    # paste the file, then save it" class="mb-4" />
    <p class="text-muted-foreground mb-8">
        Optional bot integrations are commented out. To use one, remove the <code class={CODE}>#</code> and add its value.
    </p>

    {#if hostData}
        <h3 class="text-xl font-semibold mt-6 mb-3">Set the PostgreSQL data directory</h3>
        <ol class="list-decimal list-inside space-y-3 text-muted-foreground mb-4">
            <li>
                Create the directory on the machine running the Docker daemon, not merely on the
                computer where you run the Docker CLI. Use an absolute path and make sure the PostgreSQL container can write to it. For example:
                <CodeBlock code="sudo mkdir -p /srv/nocturne/postgres-data" class="mt-3" />
            </li>
            <li>
                Add the path to <code class={CODE}>.env</code>:
                <CodeBlock code="POSTGRES_DATA_PATH=/srv/nocturne/postgres-data" class="mt-3" />
            </li>
        </ol>
        <p class="text-muted-foreground mb-4">
            The override requires <code class={CODE}>POSTGRES_DATA_PATH</code>
            and refuses to create a missing host directory. If the variable is unset or the path does
            not exist, Compose reports an error instead of silently starting PostgreSQL in a newly
            created directory. A path that exists but contains no database is still a new database.
        </p>
        <Callout type="warning" title="Changing the path does not move your database">
            <p>
                A new or empty directory initializes a separate, empty PostgreSQL database. For an
                existing install, take and verify a backup, stop the stack, and migrate the existing
                PostgreSQL data before starting with the override. Preserve file ownership and use
                the same PostgreSQL major version. Keep the original named volume until the new
                location is verified. There is no automatic volume migration. Do not use
                <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">docker compose down -v</code>
                while the original volume is your only copy.
            </p>
        </Callout>
        <details class="mb-3">
            <summary class={SUMMARY}>Move an existing database from the named volume</summary>
            <div class="mt-3">
                <p class="text-muted-foreground mb-4">
                    Schedule downtime first. This copies PostgreSQL's physical data directory, so
                    the source server must be stopped and the destination must use the same
                    PostgreSQL major version. These commands assume Docker runs on this Linux host
                    and you are in the directory containing the Compose files and <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">.env</code>.
                </p>
                <ol class="list-decimal list-inside space-y-3 text-muted-foreground mb-4">
                    <li>
                        Stop the stack without removing its containers or volumes, then record the
                        stopped PostgreSQL container:
                        <CodeBlock code={stopCommand} class="mt-3" />
                    </li>
                    <li>
                        Create and inspect a root-owned backup archive while the database is stopped:
                        <CodeBlock code={'docker run --rm --user 0 --volumes-from "$postgres_container" -v "$PWD:/backup" postgres:17.6 tar --numeric-owner -C /var/lib/postgresql -cpf /backup/nocturne-postgres-data.tar data\ntar -tf nocturne-postgres-data.tar | head'} class="mt-3" />
                        Keep this archive somewhere safe before continuing.
                    </li>
                    <li>
                        Create the host directory and copy the stopped data directory, preserving
                        its contents and ownership:
                        <CodeBlock code={'sudo mkdir -p /srv/nocturne/postgres-data\ndocker run --rm --user 0 --volumes-from "$postgres_container" -v /srv/nocturne/postgres-data:/target postgres:17.6 sh -c \'cp -a /var/lib/postgresql/data/. /target/ && chown --reference=/var/lib/postgresql/data /target && chmod --reference=/var/lib/postgresql/data /target\''} class="mt-3" />
                    </li>
                    <li>
                        Set <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">POSTGRES_DATA_PATH=/srv/nocturne/postgres-data</code>
                        in <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">.env</code> and start
                        with the override:
                        <CodeBlock code={upCommand} class="mt-3" />
                    </li>
                    <li>
                        Verify the PostgreSQL logs and your existing Nocturne data before allowing
                        writes. Keep the original named volume and backup archive until verification
                        is complete. To roll back before new writes, stop the override stack without
                        deleting volumes. Then start the original configuration again:
                        <CodeBlock code={rollbackCommand} class="mt-3" />
                    </li>
                </ol>
                <p class="text-muted-foreground">
                    Do not use <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">down -v</code>
                    during migration or rollback. It deletes named volumes. If the new database has
                    accepted writes, it is no longer equivalent to the untouched original volume.
                    Plan any rollback or restore before reopening the service to users.
                </p>
            </div>
        </details>
        <details class="mb-8">
            <summary class={SUMMARY}>View docker-compose.bind-data.yaml</summary>
            <CodeBlock code={bindDataCompose} class="mt-2" maxHeight="240px" />
        </details>
    {/if}

    <StepHeading id="start" installStep="3">Step 3: Start the services</StepHeading>
    <CodeBlock code={upCommand} class="mb-4" />
    <p class="text-muted-foreground mb-4">
        Docker will pull the images and start all services. First run takes a few minutes.
    </p>
    {#if overrides}
        <p class="text-muted-foreground mb-4">
            Keep every <code class={CODE}>-f</code> argument, in this order, on every later
            Compose command for this stack. The commands on this page include them. If you use a
            local override file, add its <code class={CODE}>-f</code> argument also. If you omit
            one, Compose can restore the named-volume mount or the bundled proxy configuration.
        </p>
    {/if}
    {#if ownProxy}
        <p class="text-muted-foreground mb-8">
            The bring-your-own-proxy override disables the bundled Caddy and exposes the gateway
            on plain HTTP port 8080 for your proxy to forward to. Your proxy must forward the
            original <code class={CODE}>Host</code>
            along with <code class={CODE}>X-Forwarded-Proto</code> and
            <code class={CODE}>X-Forwarded-Host</code>, or logins will
            fail with a 403. See
            <a href={resolve("docs/installation/reverse-proxy")} class="text-primary hover:underline">Bring your own reverse proxy</a>
            for worked nginx, Traefik, and Caddy configurations.
        </p>
    {:else}
        <p class="text-muted-foreground mb-8">
            HTTPS is automatic. The bundled Caddy reverse proxy obtains and renews Let's Encrypt
            TLS certificates, with no API keys or certificate files to manage. The certificate
            for <Domain /> is issued on first start, and each tenant subdomain gets one on demand
            the first time it is visited.
        </p>
    {/if}

    <StepHeading id="verify" installStep="4">Step 4: Verify the installation</StepHeading>
    <VerificationSteps {compose} {ownProxy} />

    <h2 data-install-step="next-steps" class="text-2xl font-bold mt-12 mb-4">Next steps</h2>
    <NextSteps />

    <h2 class="text-2xl font-bold mt-12 mb-4">After the installation</h2>
    <details class="mb-3">
        <summary class={SUMMARY}>Update Nocturne</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Watchtower checks for image updates daily, except for Caddy and Postgres. To update everything, including Caddy and Postgres:
        </p>
        <CodeBlock code={updateCommand} class="mb-4" />
        <p class="text-muted-foreground mb-4">
            When a new release is available, each tenant owner gets a notification with the release notes.
            To show the time that Watchtower installs the release, set
            <code class={CODE}>WATCHTOWER_API_TOKEN</code> in your
            <code class={CODE}>.env</code> file to a value from the generator. Then run
            <code class={CODE}>{compose} up -d</code>. This setting is optional.
            If you installed before this setting was added, download the new bundle first.
        </p>
        <p class="text-muted-foreground mb-4">
            Watchtower updates images, not the compose file. If you installed before the
            restart policy and log rotation below were added, download the new bundle and
            run <code class={CODE}>{compose} up -d</code>
            to pick them up. That first run recreates the containers, which clears their
            old logs. Your database data is kept. If you installed before Caddy was excluded from Watchtower, set <code class={CODE}>com.centurylinklabs.watchtower.enable</code>
            to <code class={CODE}>"false"</code> on the <code class={CODE}>caddy</code> service. Or download the new bundle and run
            <code class={CODE}>{compose} up -d</code> to recreate it.
        </p>
        {#if hostData}
            <p class="text-muted-foreground mb-4">
                Omitting the bind-data override changes the declared storage back to the named volume.
            </p>
        {/if}
    </details>
    <details class="mb-3">
        <summary class={SUMMARY}>Restarts and logs</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Every service sets <code class={CODE}>restart: unless-stopped</code>,
            so Docker restarts it when it exits or when the Docker daemon restarts. Docker does not restart a service that you stopped. Docker's default policy is <code class={CODE}>no</code>
            (<a href="https://docs.docker.com/engine/containers/start-containers-automatically/" class="text-primary hover:underline">Docker docs</a>).
        </p>
        <p class="text-muted-foreground mb-4">
            Every service also logs to the <code class={CODE}>json-file</code>
            driver, rotated at three files of 10 MB. Left to its defaults, that driver never
            rotates, because <code class={CODE}>max-size</code>
            defaults to unlimited
            (<a href="https://docs.docker.com/engine/logging/drivers/json-file/" class="text-primary hover:underline">Docker docs</a>).
            A logging driver set on a container replaces the daemon's default
            (<a href="https://docs.docker.com/engine/logging/configure/" class="text-primary hover:underline">Docker docs</a>),
            so the bundle overrides any <code class={CODE}>log-driver</code>
            in your <code class={CODE}>daemon.json</code>.
            To keep yours, set <code class={CODE}>logging</code>
            on the services in a <code class={CODE}>docker-compose.override.yaml</code>. Compose reads that file only when you pass no <code class={CODE}>-f</code> (<a href="https://docs.docker.com/compose/how-tos/multiple-compose-files/merge/" class="text-primary hover:underline">Docker docs</a>).
            {#if overrides}
                Your commands pass <code class={CODE}>-f</code>, so also pass
                <code class={CODE}>-f docker-compose.override.yaml</code>.
            {/if}
        </p>
    </details>
    <details class="mb-3">
        <summary class={SUMMARY}>If you lose the .env file</summary>
        <p class="text-muted-foreground mt-2 mb-4">
            Keep a copy of <code class={CODE}>.env</code> with your database backups. A database backup does not
            contain these values.
        </p>
        <p class="text-muted-foreground mb-2">
            <code class={CODE}>INSTANCE_KEY</code>: you cannot recover it. With a new key:
        </p>
        <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-4">
            <li>Alerts stop at Discord, webhooks and the other channels that use a secret, until you enter each secret again.</li>
            <li>Connectors stop until you enter their passwords again.</li>
            <li>Existing share links still work, but Nocturne cannot show them again. To see a link, make a new one.</li>
        </ul>
        <p class="text-muted-foreground mb-4">
            Your glucose data, treatments and settings stay in the database.
        </p>
        <p class="text-muted-foreground mb-4">
            The database passwords: you can set new ones. Put new values in
            <code class={CODE}>.env</code>, then do these steps to set the same values in the database.
        </p>
        <ol class="list-decimal list-inside space-y-3 text-muted-foreground mb-4">
            <li>
                Let PostgreSQL accept connections from inside its container without a password:
                <CodeBlock code={allowLocalLogin} class="mt-2" />
            </li>
            <li>
                Set the new passwords. Replace each value in angle brackets with the value from your
                new <code class={CODE}>.env</code>. If you changed <code class={CODE}>POSTGRES_USERNAME</code>, use that name in
                place of <code class={CODE}>nocturne</code>.
                <CodeBlock code={resetPasswords} class="mt-2" />
            </li>
            <li>
                Remove the line that you added in step 1, and start Nocturne with the new values:
                <CodeBlock code={removeLocalLogin} class="mt-2" />
            </li>
        </ol>
    </details>
    <details class="mb-8">
        <summary class={SUMMARY}>Troubleshooting</summary>
        <p class="text-muted-foreground mt-2 mb-4">Check the logs for error details:</p>
        <CodeBlock code={logsCommand} class="mb-4" />
        <p class="text-muted-foreground mb-4">To start fresh, stop all services and remove volumes:</p>
        <CodeBlock code={resetCommand} class="mb-4" />
        {#if hostData}
            <p class="text-muted-foreground mb-4">
                This command does not delete the database in your host directory. A stack that
                you start again uses that database and its passwords.
            </p>
        {/if}
    </details>

    <SupportNocturne />
</div>
