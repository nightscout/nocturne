<script lang="ts">
    import type { GenerateRequest, ConnectorMetadata } from "$lib/data/portal.remote";
    import { getConnectors } from "$lib/data/portal.remote";
    import { Button } from "@nocturne/app/ui/button";
    import { Input } from "@nocturne/app/ui/input";
    import { Label } from "@nocturne/app/ui/label";
    import * as Card from "@nocturne/app/ui/card";
    import { ChevronLeft, Download, CheckCircle, AlertCircle, Settings, Server } from "@lucide/svelte";
    import { page } from "$app/state";
    import { SetupParamsSchema, setupTypeLabels } from "$lib/schemas/setup-params";
    import ConnectorConfigDialog from "$lib/components/ConnectorConfigDialog.svelte";
    import {
        getConnectorConfigs,
        setConnectorConfigs,
    } from "$lib/stores/wizard-store.svelte";

    // Parse and validate URL params using the shared schema
    const params = $derived.by(() => {
        const searchParams = page.url.searchParams;
        const raw = Object.fromEntries(searchParams.entries());
        return SetupParamsSchema.parse(raw);
    });

    // Parse connectors from URL
    const selectedConnectors = $derived(
        params.connectors?.split(",").filter(Boolean) ?? [],
    );

    // Load connector metadata
    const connectorsQuery = getConnectors({});

    // Use shared store for connector configs (sensitive data, not in URL)
    let connectorConfigs = $state<Record<string, Record<string, string>>>(getConnectorConfigs());

    // Sync local state to shared store when it changes
    $effect(() => {
        setConnectorConfigs(connectorConfigs);
    });

    // Local state for editable config values
    let nightscoutUrl = $state(params.nightscoutUrl ?? "");
    let nightscoutApiSecret = $state(params.nightscoutApiSecret ?? "");
    let mongoConnectionString = $state(params.mongoConnectionString ?? "");
    let mongoDatabaseName = $state(params.mongoDatabaseName ?? "");

    // Sync initial values from URL params
    $effect(() => {
        nightscoutUrl = params.nightscoutUrl ?? "";
        nightscoutApiSecret = params.nightscoutApiSecret ?? "";
        mongoConnectionString = params.mongoConnectionString ?? "";
        mongoDatabaseName = params.mongoDatabaseName ?? "";
    });

    // Connector config dialog state
    let configuringConnector = $state<ConnectorMetadata | null>(null);
    let isConfigDialogOpen = $state(false);

    // Check if nightscout config has any values filled
    const hasNightscoutConfig = $derived(
        nightscoutUrl.trim() !== "" || nightscoutApiSecret.trim() !== ""
    );

    // Check if mongo config has any values filled (for migrate mode)
    const hasMongoConfig = $derived(
        mongoConnectionString.trim() !== "" || mongoDatabaseName.trim() !== ""
    );

    // Check if a connector has any config values filled
    function hasConnectorConfig(connectorType: string): boolean {
        const config = connectorConfigs[connectorType];
        if (!config) return false;
        return Object.values(config).some((v) => v && v.trim() !== "");
    }

    let generating = $state(false);
    let error = $state<string | null>(null);

    async function handleDownload() {
        generating = true;
        error = null;

        try {
            const request: GenerateRequest = {
                setupType: params.type ?? "fresh",
                postgres: {
                    useContainer: params.useContainer,
                    connectionString: params.useContainer
                        ? undefined
                        : params.connectionString ?? undefined,
                },
                optionalServices: {
                    watchtower: { enabled: params.watchtower },
                    aspireDashboard: { enabled: params.includeDashboard },
                    scalar: { enabled: params.includeScalar },
                },
                connectors: selectedConnectors.map((type) => ({
                    type,
                    config: connectorConfigs[type] ?? {},
                })),
                migration:
                    params.type === "migrate"
                        ? {
                              mode: params.migrationMode,
                              nightscoutUrl: nightscoutUrl,
                              nightscoutApiSecret: nightscoutApiSecret,
                              mongoConnectionString:
                                  params.migrationMode === "MongoDb"
                                      ? mongoConnectionString
                                      : undefined,
                              mongoDatabaseName:
                                  params.migrationMode === "MongoDb"
                                      ? mongoDatabaseName
                                      : undefined,
                          }
                        : undefined,
                compatibilityProxy:
                    params.type === "compatibility-proxy"
                        ? {
                              nightscoutUrl: nightscoutUrl,
                              nightscoutApiSecret: nightscoutApiSecret,
                              enableDetailedLogging: params.enableDetailedLogging,
                          }
                        : undefined,
            };

            // Direct fetch call for file download - remote functions can't serialize blobs
            const response = await fetch("/api/generate", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(request),
            });

            if (!response.ok) {
                const errorText = await response.text();
                throw new Error(
                    errorText ||
                        `Failed to generate config: ${response.statusText}`,
                );
            }

            const blob = await response.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = "nocturne-config.zip";
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        } catch (e) {
            error =
                e instanceof Error
                    ? e.message
                    : "Failed to generate configuration";
        } finally {
            generating = false;
        }
    }

    function openConnectorConfig(connector: ConnectorMetadata) {
        configuringConnector = connector;
        isConfigDialogOpen = true;
    }

    function handleConnectorConfigSave(
        connector: ConnectorMetadata,
        values: Record<string, string>,
    ) {
        connectorConfigs = {
            ...connectorConfigs,
            [connector.type]: values,
        };
    }

    const setupTypeLabel = $derived(
        params.type ? setupTypeLabels[params.type] : "Unknown"
    );

    const backUrl = $derived.by(() => {
        // Preserve all params when going back
        const searchParams = new URLSearchParams(page.url.searchParams);
        searchParams.set("step", "2");
        return `/setup?${searchParams.toString()}`;
    });

    // Whether nightscout config is needed (migrate or compatibility-proxy)
    const needsNightscoutConfig = $derived(
        params.type === "migrate" || params.type === "compatibility-proxy"
    );

    // Whether mongo config is needed (migrate with MongoDb mode)
    const needsMongoConfig = $derived(
        params.type === "migrate" && params.migrationMode === "MongoDb"
    );
</script>

<div class="max-w-2xl mx-auto">
    <Button
        href={backUrl}
        variant="ghost"
        size="sm"
        class="mb-8 gap-1 text-muted-foreground"
    >
        <ChevronLeft size={16} />
        Back to connectors
    </Button>

    <h1 class="text-3xl font-bold mb-2">Review Configuration</h1>
    <p class="text-muted-foreground mb-8">
        Verify your settings and download deployment files
    </p>

    <div class="space-y-6 mb-8">
        <Card.Root>
            <Card.Header>
                <Card.Title>Setup Type</Card.Title>
            </Card.Header>
            <Card.Content>
                <div class="flex items-center gap-3">
                    <CheckCircle size={20} class="text-primary" />
                    <span class="font-medium">{setupTypeLabel}</span>
                </div>
            </Card.Content>
        </Card.Root>

        {#if needsNightscoutConfig}
            <Card.Root>
                <Card.Header class="flex flex-row items-center justify-between">
                    <Card.Title class="flex items-center gap-2">
                        <Server size={18} />
                        Nightscout Instance
                    </Card.Title>
                    {#if hasNightscoutConfig}
                        <CheckCircle size={18} class="text-green-500" />
                    {:else}
                        <AlertCircle size={18} class="text-amber-500" />
                    {/if}
                </Card.Header>
                <Card.Content class="space-y-4">
                    {#if !hasNightscoutConfig}
                        <p class="text-sm text-amber-600 dark:text-amber-400">
                            You'll need to add your configuration to the .env file
                        </p>
                    {/if}
                    <div class="space-y-3">
                        <div class="space-y-1.5">
                            <Label for="nightscoutUrl" class="text-sm">Nightscout URL</Label>
                            <Input
                                id="nightscoutUrl"
                                type="url"
                                bind:value={nightscoutUrl}
                                placeholder="https://my-site.herokuapp.com"
                            />
                        </div>
                        <div class="space-y-1.5">
                            <Label for="nightscoutApiSecret" class="text-sm">API Secret</Label>
                            <Input
                                id="nightscoutApiSecret"
                                type="password"
                                bind:value={nightscoutApiSecret}
                                placeholder="Your API secret"
                            />
                        </div>
                    </div>
                </Card.Content>
            </Card.Root>
        {/if}

        {#if needsMongoConfig}
            <Card.Root>
                <Card.Header class="flex flex-row items-center justify-between">
                    <Card.Title>MongoDB Connection</Card.Title>
                    {#if hasMongoConfig}
                        <CheckCircle size={18} class="text-green-500" />
                    {:else}
                        <AlertCircle size={18} class="text-amber-500" />
                    {/if}
                </Card.Header>
                <Card.Content class="space-y-4">
                    {#if !hasMongoConfig}
                        <p class="text-sm text-amber-600 dark:text-amber-400">
                            You'll need to add your configuration to the .env file
                        </p>
                    {/if}
                    <div class="space-y-3">
                        <div class="space-y-1.5">
                            <Label for="mongoConnectionString" class="text-sm">Connection String</Label>
                            <Input
                                id="mongoConnectionString"
                                type="password"
                                bind:value={mongoConnectionString}
                                placeholder="mongodb+srv://user:pass@cluster.mongodb.net"
                                class="font-mono text-sm"
                            />
                        </div>
                        <div class="space-y-1.5">
                            <Label for="mongoDatabaseName" class="text-sm">Database Name</Label>
                            <Input
                                id="mongoDatabaseName"
                                bind:value={mongoDatabaseName}
                                placeholder="nightscout"
                            />
                        </div>
                    </div>
                </Card.Content>
            </Card.Root>
        {/if}

        <Card.Root>
            <Card.Header>
                <Card.Title>Database</Card.Title>
            </Card.Header>
            <Card.Content>
                <p class="text-muted-foreground">
                    {params.useContainer
                        ? "✓ Using included PostgreSQL container"
                        : "✓ External database configured"}
                </p>
            </Card.Content>
        </Card.Root>

        <Card.Root>
            <Card.Header>
                <Card.Title>Connectors</Card.Title>
            </Card.Header>
            <Card.Content>
                {#if selectedConnectors.length === 0}
                    <p class="text-muted-foreground">No connectors selected</p>
                {:else}
                    {#await connectorsQuery}
                        <div class="flex justify-center py-4">
                            <div class="animate-spin w-5 h-5 border-2 border-primary border-t-transparent rounded-full"></div>
                        </div>
                    {:then allConnectors}
                        <ul class="space-y-3">
                            {#each selectedConnectors as connectorType}
                                {@const connector = allConnectors.find((c) => c.type === connectorType)}
                                <li class="flex items-center justify-between">
                                    <div class="flex items-center gap-2">
                                        {#if hasConnectorConfig(connectorType)}
                                            <CheckCircle size={16} class="text-green-500" />
                                        {:else}
                                            <AlertCircle size={16} class="text-amber-500" />
                                        {/if}
                                        <span class="text-muted-foreground">
                                            {connector?.displayName ?? connectorType}
                                        </span>
                                        {#if !hasConnectorConfig(connectorType)}
                                            <span class="text-xs text-amber-600 dark:text-amber-400">
                                                (needs config)
                                            </span>
                                        {/if}
                                    </div>
                                    {#if connector && connector.fields.length > 0}
                                        <Button
                                            variant="ghost"
                                            size="sm"
                                            class="gap-1.5 h-8"
                                            onclick={() => openConnectorConfig(connector)}
                                        >
                                            <Settings size={14} />
                                            Configure
                                        </Button>
                                    {/if}
                                </li>
                            {/each}
                        </ul>
                    {:catch}
                        <ul class="space-y-2">
                            {#each selectedConnectors as connector}
                                <li class="flex items-center gap-2 text-muted-foreground">
                                    <AlertCircle size={16} class="text-amber-500" />
                                    {connector}
                                </li>
                            {/each}
                        </ul>
                    {/await}
                {/if}
            </Card.Content>
        </Card.Root>

        <Card.Root>
            <Card.Header>
                <Card.Title>Optional Services</Card.Title>
            </Card.Header>
            <Card.Content>
                <ul class="space-y-1 text-muted-foreground">
                    <li>
                        {params.watchtower ? "✓" : "○"} Watchtower auto-updates
                    </li>
                    <li>
                        {params.includeDashboard ? "✓" : "○"} Aspire Dashboard
                    </li>
                    <li>
                        {params.includeScalar ? "✓" : "○"} Scalar API docs
                    </li>
                </ul>
            </Card.Content>
        </Card.Root>
    </div>

    {#if error}
        <div
            class="p-4 rounded-lg bg-red-500/10 border border-red-500/50 text-red-400 mb-6"
        >
            <p class="font-medium">Generation failed</p>
            <p class="text-sm opacity-80">{error}</p>
        </div>
    {/if}

    <div class="flex justify-center">
        <Button
            onclick={handleDownload}
            disabled={generating}
            size="lg"
            class="gap-3"
        >
            {#if generating}
                <div
                    class="animate-spin w-5 h-5 border-2 border-current border-t-transparent rounded-full"
                ></div>
                Generating...
            {:else}
                <Download size={24} />
                Download Configuration
            {/if}
        </Button>
    </div>

    <Card.Root class="mt-8 bg-muted/30">
        <Card.Header>
            <Card.Title class="text-base">After downloading:</Card.Title>
        </Card.Header>
        <Card.Content>
            <ol
                class="list-decimal list-inside space-y-2 text-muted-foreground text-sm"
            >
                <li>Extract the ZIP file to your server</li>
                <li>
                    Fill in any empty values in <code class="text-primary"
                        >.env</code
                    >
                </li>
                <li>
                    Run <code class="text-primary">docker compose up -d</code>
                </li>
                <li>
                    Access Nocturne at <code class="text-primary"
                        >http://localhost:1337</code
                    >
                </li>
            </ol>
        </Card.Content>
    </Card.Root>
</div>

<ConnectorConfigDialog
    open={isConfigDialogOpen}
    connector={configuringConnector}
    initialValues={configuringConnector ? connectorConfigs[configuringConnector.type] ?? {} : {}}
    onOpenChange={(open) => {
        isConfigDialogOpen = open;
        if (!open) configuringConnector = null;
    }}
    onSave={handleConnectorConfigSave}
/>
