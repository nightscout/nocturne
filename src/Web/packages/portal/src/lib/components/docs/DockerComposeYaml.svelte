<script lang="ts">
    interface Props {
        /** Show only the minimal core stack (postgres + api + watchtower) */
        minimal?: boolean;
    }

    let { minimal = false }: Props = $props();

    const minimalCompose = `services:
  nocturne-postgres-server:
    image: "docker.io/library/postgres:17.6"
    environment:
      POSTGRES_HOST_AUTH_METHOD: "scram-sha-256"
      POSTGRES_INITDB_ARGS: "--auth-host=scram-sha-256 --auth-local=scram-sha-256"
      POSTGRES_USER: "\${POSTGRES_USERNAME}"
      POSTGRES_PASSWORD: "\${POSTGRES_PASSWORD}"
      NOCTURNE_MIGRATOR_PASSWORD: "\${NOCTURNE_MIGRATOR_PASSWORD}"
      NOCTURNE_APP_PASSWORD: "\${NOCTURNE_APP_PASSWORD}"
      NOCTURNE_WEB_PASSWORD: "\${NOCTURNE_WEB_PASSWORD}"
    expose:
      - "5432"
    volumes:
      - type: "volume"
        target: "/var/lib/postgresql/data"
        source: "nocturne-postgres-data"
      - type: "bind"
        source: "./pg-init"
        target: "/docker-entrypoint-initdb.d"
        read_only: true
    networks:
      - "nocturne"
    restart: "unless-stopped"

  nocturne-api:
    image: "\${NOCTURNE_API_IMAGE}"
    environment:
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      HTTPS_PORTS: "\${NOCTURNE_API_PORT}"
      ConnectionStrings__nocturne-postgres: "Host=nocturne-postgres-server;Port=5432;Username=nocturne_app;Password=\${NOCTURNE_APP_PASSWORD};Database=nocturne"
      ConnectionStrings__nocturne-postgres-migrator: "Host=nocturne-postgres-server;Port=5432;Username=nocturne_migrator;Password=\${NOCTURNE_MIGRATOR_PASSWORD};Database=nocturne"
      INSTANCE_KEY: "\${INSTANCE_KEY}"
    ports:
      - "\${NOCTURNE_API_PORT}:\${NOCTURNE_API_PORT}"
    depends_on:
      nocturne-postgres-server:
        condition: "service_started"
    networks:
      - "nocturne"
    restart: "unless-stopped"

  watchtower:
    image: "ghcr.io/nicholas-fedor/watchtower:latest"
    environment:
      WATCHTOWER_CLEANUP: "true"
      WATCHTOWER_POLL_INTERVAL: "86400"
    volumes:
      - "/var/run/docker.sock:/var/run/docker.sock:ro"
    networks:
      - "nocturne"
    restart: "unless-stopped"

networks:
  nocturne:
    driver: "bridge"

volumes:
  nocturne-postgres-data:
    driver: "local"`;

    const fullCompose = `services:
  nocturne-postgres-server:
    image: "docker.io/library/postgres:17.6"
    environment:
      POSTGRES_HOST_AUTH_METHOD: "scram-sha-256"
      POSTGRES_INITDB_ARGS: "--auth-host=scram-sha-256 --auth-local=scram-sha-256"
      POSTGRES_USER: "\${POSTGRES_USERNAME}"
      POSTGRES_PASSWORD: "\${POSTGRES_PASSWORD}"
      NOCTURNE_MIGRATOR_PASSWORD: "\${NOCTURNE_MIGRATOR_PASSWORD}"
      NOCTURNE_APP_PASSWORD: "\${NOCTURNE_APP_PASSWORD}"
      NOCTURNE_WEB_PASSWORD: "\${NOCTURNE_WEB_PASSWORD}"
    expose:
      - "5432"
    volumes:
      - type: "volume"
        target: "/var/lib/postgresql/data"
        source: "nocturne-postgres-data"
      - type: "bind"
        source: "./pg-init"
        target: "/docker-entrypoint-initdb.d"
        read_only: true
    networks:
      - "nocturne"
    restart: "unless-stopped"

  nocturne-api:
    image: "\${NOCTURNE_API_IMAGE}"
    environment:
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      HTTPS_PORTS: "\${NOCTURNE_API_PORT}"
      ConnectionStrings__nocturne-postgres: "Host=nocturne-postgres-server;Port=5432;Username=nocturne_app;Password=\${NOCTURNE_APP_PASSWORD};Database=nocturne"
      ConnectionStrings__nocturne-postgres-migrator: "Host=nocturne-postgres-server;Port=5432;Username=nocturne_migrator;Password=\${NOCTURNE_MIGRATOR_PASSWORD};Database=nocturne"
      INSTANCE_KEY: "\${INSTANCE_KEY}"
      DemoService__Enabled: "false"
    ports:
      - "\${NOCTURNE_API_PORT}:\${NOCTURNE_API_PORT}"
    depends_on:
      nocturne-postgres-server:
        condition: "service_started"
    networks:
      - "nocturne"
    restart: "unless-stopped"

  # Optional: Dexcom connector
  dexcom-connector:
    image: "\${DEXCOM_CONNECTOR_IMAGE}"
    environment:
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      HTTP_PORTS: "\${DEXCOM_CONNECTOR_PORT}"
      NocturneApiUrl: "https://nocturne-api:\${NOCTURNE_API_PORT}"
      INSTANCE_KEY: "\${INSTANCE_KEY}"
      CONNECT_DEXCOM_USERNAME: "\${DEXCOM_USERNAME}"
      CONNECT_DEXCOM_PASSWORD: "\${DEXCOM_PASSWORD}"
      CONNECT_DEXCOM_SERVER: "\${DEXCOM_SERVER}"
      Parameters__Connectors__Dexcom__Enabled: "true"
    expose:
      - "\${DEXCOM_CONNECTOR_PORT}"
    depends_on:
      nocturne-api:
        condition: "service_started"
    networks:
      - "nocturne"
    restart: "unless-stopped"

  # Optional: LibreLinkUp connector
  freestyle-connector:
    image: "\${FREESTYLE_CONNECTOR_IMAGE}"
    environment:
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      HTTP_PORTS: "\${FREESTYLE_CONNECTOR_PORT}"
      NocturneApiUrl: "https://nocturne-api:\${NOCTURNE_API_PORT}"
      INSTANCE_KEY: "\${INSTANCE_KEY}"
      CONNECT_LIBRE_USERNAME: "\${LIBRELINKUP_USERNAME}"
      CONNECT_LIBRE_PASSWORD: "\${LIBRELINKUP_PASSWORD}"
      CONNECT_LIBRE_REGION: "\${LIBRELINKUP_REGION}"
      Parameters__Connectors__LibreLinkUp__Enabled: "true"
    expose:
      - "\${FREESTYLE_CONNECTOR_PORT}"
    depends_on:
      nocturne-api:
        condition: "service_started"
    networks:
      - "nocturne"
    restart: "unless-stopped"

  watchtower:
    image: "ghcr.io/nicholas-fedor/watchtower:latest"
    environment:
      WATCHTOWER_CLEANUP: "true"
      WATCHTOWER_POLL_INTERVAL: "86400"
    volumes:
      - "/var/run/docker.sock:/var/run/docker.sock:ro"
    networks:
      - "nocturne"
    restart: "unless-stopped"

networks:
  nocturne:
    driver: "bridge"

volumes:
  nocturne-postgres-data:
    driver: "local"`;

    const envTemplate = `# Core settings
# Bootstrap superuser — only used by the Postgres image at first start to
# create nocturne_migrator, nocturne_app, and nocturne_web via the bundled
# pg-init/00-init.sh.
POSTGRES_USERNAME=nocturne_bootstrap
POSTGRES_PASSWORD=change-me-bootstrap-password
# Nocturne runtime roles. The migrator owns the schema and runs migrations;
# the app role runs at request time with NOBYPASSRLS so Row Level Security
# enforces tenant isolation; the web role owns the SvelteKit bot framework's
# chat_state_* tables. Do not reuse the bootstrap password here.
NOCTURNE_MIGRATOR_PASSWORD=change-me-migrator-password
NOCTURNE_APP_PASSWORD=change-me-app-password
NOCTURNE_WEB_PASSWORD=change-me-web-password
INSTANCE_KEY=change-me-min-12-characters
NOCTURNE_API_PORT=8443
NOCTURNE_API_IMAGE=ghcr.io/nightscout/nocturne-api:latest

# Dexcom connector (optional)
DEXCOM_CONNECTOR_IMAGE=ghcr.io/nightscout/nocturne-dexcom:latest
DEXCOM_CONNECTOR_PORT=8081
DEXCOM_USERNAME=
DEXCOM_PASSWORD=
DEXCOM_SERVER=us

# LibreLinkUp connector (optional)
FREESTYLE_CONNECTOR_IMAGE=ghcr.io/nightscout/nocturne-freestyle:latest
FREESTYLE_CONNECTOR_PORT=8082
LIBRELINKUP_USERNAME=
LIBRELINKUP_PASSWORD=
LIBRELINKUP_REGION=eu`;

    const compose = minimal ? minimalCompose : fullCompose;
</script>

<div class="space-y-6 mb-8">
    <div class="p-4 rounded-lg border border-amber-500/30 bg-amber-500/5 text-sm text-foreground">
        <p class="font-medium mb-1">Three-role PostgreSQL setup</p>
        <p class="text-muted-foreground">
            Nocturne uses three separate PostgreSQL users for defense-in-depth against PHI leakage.
            <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_migrator</code> runs schema
            migrations; <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_app</code> runs at
            request time with no DDL privileges and cannot bypass Row Level Security;
            <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_web</code> owns the
            SvelteKit bot framework's <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">chat_state_*</code>
            tables. The init script mounted at
            <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">./pg-init</code> creates all three
            roles at first container start. Do not consolidate them.
        </p>
    </div>
    <div>
        <p class="text-sm font-medium text-foreground mb-2">docker-compose.yml</p>
        <pre class="p-4 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto max-h-[500px]"><code>{compose}</code></pre>
    </div>

    <div>
        <p class="text-sm font-medium text-foreground mb-2">.env</p>
        <pre class="p-4 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto"><code>{envTemplate}</code></pre>
    </div>
</div>
