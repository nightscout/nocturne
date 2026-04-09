<script lang="ts" module>
    const coreVars = [
        { name: "POSTGRES_USERNAME", description: "PostgreSQL bootstrap superuser. Only used by the Postgres image at first container start to create the nocturne_migrator and nocturne_app roles.", example: "nocturne_bootstrap" },
        { name: "POSTGRES_PASSWORD", description: "Password for the bootstrap superuser above.", example: "your-bootstrap-password" },
        { name: "NOCTURNE_MIGRATOR_PASSWORD", description: "Password for the nocturne_migrator role. This role owns the schema and runs EF migrations on startup.", example: "your-migrator-password" },
        { name: "NOCTURNE_APP_PASSWORD", description: "Password for the nocturne_app role. This is the runtime connection and cannot bypass Row Level Security.", example: "your-app-password" },
        { name: "NOCTURNE_WEB_PASSWORD", description: "Password for the nocturne_web role. Used by the SvelteKit web app's bot framework to store chat-platform state in its own non-tenant-scoped tables.", example: "your-web-password" },
        { name: "NOCTURNE_POSTGRES", description: "Runtime connection string (app role). Exposed to services as ConnectionStrings__nocturne-postgres.", example: "Host=nocturne-postgres-server;Port=5432;Username=nocturne_app;Password=...;Database=nocturne" },
        { name: "NOCTURNE_POSTGRES_MIGRATOR", description: "Migration connection string (migrator role). Exposed to services as ConnectionStrings__nocturne-postgres-migrator.", example: "Host=nocturne-postgres-server;Port=5432;Username=nocturne_migrator;Password=...;Database=nocturne" },
        { name: "NOCTURNE_POSTGRES_URI", description: "postgresql:// URL for the nocturne_web role. Consumed by the SvelteKit web app's bot state adapter (@chat-adapter/state-pg).", example: "postgresql://nocturne_web:...@nocturne-postgres-server:5432/nocturne" },
        { name: "INSTANCE_KEY", description: "Instance key for JWT signing, encryption, and service authentication", example: "your-instance-key-min-12-chars" },
        { name: "NOCTURNE_API_PORT", description: "Port for the Nocturne API service", example: "8443" },
        { name: "NOCTURNE_API_IMAGE", description: "Docker image for the Nocturne API", example: "ghcr.io/nightscout/nocturne-api:latest" },
    ];

    const connectorVars = [
        { name: "DEXCOM_CONNECTOR_IMAGE", description: "Docker image for the Dexcom connector", example: "ghcr.io/nightscout/nocturne-dexcom:latest" },
        { name: "DEXCOM_CONNECTOR_PORT", description: "Internal port for the Dexcom connector", example: "8081" },
        { name: "DEXCOM_USERNAME", description: "Dexcom Share username", example: "your-dexcom-username" },
        { name: "DEXCOM_PASSWORD", description: "Dexcom Share password", example: "your-dexcom-password" },
        { name: "DEXCOM_SERVER", description: "Dexcom server region (us or ous)", example: "us" },
        { name: "LIBRELINKUP_USERNAME", description: "LibreLinkUp email address", example: "your-libre-email" },
        { name: "LIBRELINKUP_PASSWORD", description: "LibreLinkUp password", example: "your-libre-password" },
        { name: "LIBRELINKUP_REGION", description: "LibreLinkUp region", example: "eu" },
    ];

    const allVars = [...coreVars, ...connectorVars];
</script>

<script lang="ts">
    interface Props {
        /** Show only core vars (no connectors) */
        coreOnly?: boolean;
    }

    let { coreOnly = false }: Props = $props();

    const displayVars = coreOnly ? coreVars : allVars;
</script>

<div class="overflow-x-auto mb-8">
    <table class="w-full text-sm">
        <thead>
            <tr class="border-b border-border">
                <th class="text-left py-2 pr-4 font-semibold">Variable</th>
                <th class="text-left py-2 pr-4 font-semibold">Description</th>
                <th class="text-left py-2 font-semibold">Example</th>
            </tr>
        </thead>
        <tbody class="text-muted-foreground">
            {#each displayVars as envVar, i}
                <tr class={i < displayVars.length - 1 ? "border-b border-border/50" : ""}>
                    <td class="py-2 pr-4">
                        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">{envVar.name}</code>
                    </td>
                    <td class="py-2 pr-4">{envVar.description}</td>
                    <td class="py-2">
                        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">{envVar.example}</code>
                    </td>
                </tr>
            {/each}
        </tbody>
    </table>
    {#if coreOnly}
        <p class="text-xs text-muted-foreground mt-2">
            See the full <a href="/docs/configuration" class="text-primary hover:underline">Configuration Guide</a> for connector-specific environment variables.
        </p>
    {/if}
</div>
