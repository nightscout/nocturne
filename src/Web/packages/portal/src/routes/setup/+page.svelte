<script lang="ts">
    import { goto } from "$app/navigation";
    import { Button } from "@nocturne/app/ui/button";
    import { Input } from "@nocturne/app/ui/input";
    import { Label } from "@nocturne/app/ui/label";
    import { Switch } from "@nocturne/app/ui/switch";
    import * as Card from "@nocturne/app/ui/card";
    import * as ConnectorToggleGroup from "@nocturne/app/ui/connector-toggle-group";
    import {
        ChevronLeft,
        ChevronRight,
        Database,
        Server,
        RefreshCw,
        Cloud,
        HardDrive,
        Check,
        Loader2,
        AlertCircle,
        CheckCircle2,
        Droplet,
        Plus,
        ArrowLeftRight,
        BookOpen,
    } from "@lucide/svelte";
    import { useSearchParams } from "runed/kit";
    import {
        testNightscoutConnection,
        getConnectors,
        type ConnectorMetadata,
    } from "$lib/data/portal.remote";
    import {
        SetupParamsSchema,
        setupTypeLabels,
        setupTypeDescriptions,
    } from "$lib/schemas/setup-params";
    import ConnectorConfigDialog from "$lib/components/ConnectorConfigDialog.svelte";
    import {
        getConnectorConfigs,
        setConnectorConfigs,
        removeConnectorConfig,
    } from "$lib/stores/wizard-store.svelte";

    const params = useSearchParams(SetupParamsSchema, {
        pushHistory: false,
        debounce: 100,
        noScroll: true, // Prevent scroll to top on URL updates
    });

    // Derived state from URL params
    const currentStep = $derived(params.step);
    const setupType = $derived(params.type);
    const hasSelectedType = $derived(setupType !== null);

    // Connection test state
    type ConnectionTestState =
        | { status: "idle" }
        | { status: "testing" }
        | {
              status: "success";
              data: {
                  name?: string;
                  version?: string;
                  units?: string;
                  latestSgv?: number;
                  latestTime?: string;
              };
          }
        | { status: "error"; error: string };

    let connectionTest = $state<ConnectionTestState>({ status: "idle" });

    // Connector configuration dialog state
    let configuringConnector = $state<ConnectorMetadata | null>(null);
    let isConfigDialogOpen = $state(false);

    // Use shared store for connector configs (sensitive data, not in URL)
    let connectorConfigs = $state<Record<string, Record<string, string>>>(getConnectorConfigs());

    // Sync local state to shared store when it changes
    $effect(() => {
        setConnectorConfigs(connectorConfigs);
    });

    // Load connectors
    const connectorsQuery = getConnectors({});

    // Parse selected connectors from URL
    let selectedConnectors = $state<string[]>(
        params.connectors?.split(",").filter(Boolean) ?? [],
    );

    // Update URL and configs when selectedConnectors changes
    function handleConnectorChange(newValue: string[]) {
        selectedConnectors = newValue;
        params.connectors =
            newValue.length > 0 ? newValue.join(",") : null;

        // Update configs for newly added connectors
        newValue.forEach((type) => {
            if (!connectorConfigs[type]) {
                connectorConfigs = { ...connectorConfigs, [type]: {} };
            }
        });

        // Remove configs for removed connectors
        Object.keys(connectorConfigs).forEach((type) => {
            if (!newValue.includes(type)) {
                const { [type]: _, ...rest } = connectorConfigs;
                connectorConfigs = rest;
            }
        });
    }


    async function testConnection() {
        const url = params.nightscoutUrl;
        if (!url) {
            connectionTest = {
                status: "error",
                error: "Please enter a Nightscout URL",
            };
            return;
        }

        connectionTest = { status: "testing" };

        try {
            const result = await testNightscoutConnection({
                url,
                apiSecret: params.nightscoutApiSecret || undefined,
            });

            connectionTest = {
                status: "success",
                data: {
                    name: result.name,
                    version: result.version,
                    units: result.units,
                    latestSgv: result.latestSgv,
                    latestTime: result.latestTime
                        ? new Date(result.latestTime).toLocaleString()
                        : undefined,
                },
            };
        } catch (err) {
            connectionTest = {
                status: "error",
                error:
                    err instanceof Error
                        ? err.message
                        : "Failed to connect to Nightscout",
            };
        }
    }

    function selectSetupType(
        type: "fresh" | "migrate" | "compatibility-proxy",
    ) {
        params.type = type;
        params.step = 1;
    }

    function goToConnectors() {
        params.step = 2;
    }

    function goBackToConfig() {
        params.step = 1;
    }

    function goBackToTypeSelection() {
        params.step = 0;
        params.type = null;
    }

    function goToDownload() {
        // Build query string from current params and navigate
        const searchParams = params.toURLSearchParams();
        goto(`/download?${searchParams.toString()}`);
    }

    function openConfig(connector: ConnectorMetadata, e?: Event) {
        if (e) {
            e.stopPropagation();
            e.preventDefault();
        }
        configuringConnector = connector;
        isConfigDialogOpen = true;
    }

    function handleSaveConfig(connector: ConnectorMetadata, values: Record<string, string>) {
        connectorConfigs = {
            ...connectorConfigs,
            [connector.type]: values,
        };
    }

    const pageTitle = $derived(setupTypeLabels[setupType!]);
    const pageDescription = $derived(setupTypeDescriptions[setupType!]);

    function formatGlucose(sgv: number, units?: string): string {
        if (units === "mmol") {
            return (sgv / 18).toFixed(1) + " mmol/L";
        }
        return sgv + " mg/dL";
    }
</script>

<div class="max-w-3xl mx-auto pb-12">
    {#if currentStep === 0 && !hasSelectedType}
        <!-- Step 0: Type Selection Screen -->
        <div class="text-center mb-12">
            <h1
                class="text-4xl font-bold mb-4 bg-gradient-to-r from-primary to-primary/60 bg-clip-text text-transparent"
            >
                Configure Your Nocturne Instance
            </h1>
            <p class="text-lg text-muted-foreground">
                Choose how you'd like to set up Nocturne
            </p>
        </div>

        <div class="grid gap-4">
            <Button
                onclick={() => selectSetupType("fresh")}
                variant="ghost"
                class="h-auto p-6 justify-start text-left border border-border/50 bg-card/50 hover:bg-card hover:border-primary/50"
            >
                <div class="flex items-start gap-4 w-full">
                    <div
                        class="w-12 h-12 rounded-lg bg-primary/20 flex items-center justify-center text-primary shrink-0"
                    >
                        <Plus size={24} />
                    </div>
                    <div>
                        <h2 class="text-xl font-semibold mb-1">
                            Fresh Install
                        </h2>
                        <p class="text-muted-foreground font-normal">
                            New Nocturne instance with no existing data
                        </p>
                    </div>
                </div>
            </Button>

            <Button
                onclick={() => selectSetupType("migrate")}
                variant="ghost"
                class="h-auto p-6 justify-start text-left border border-border/50 bg-card/50 hover:bg-card hover:border-blue-500/50"
            >
                <div class="flex items-start gap-4 w-full">
                    <div
                        class="w-12 h-12 rounded-lg bg-blue-500/20 flex items-center justify-center text-blue-400 shrink-0"
                    >
                        <ArrowLeftRight size={24} />
                    </div>
                    <div>
                        <h2 class="text-xl font-semibold mb-1">
                            Migrate from Nightscout
                        </h2>
                        <p class="text-muted-foreground font-normal">
                            Import existing data from your Nightscout instance
                        </p>
                    </div>
                </div>
            </Button>

            <Button
                onclick={() => selectSetupType("compatibility-proxy")}
                variant="ghost"
                class="h-auto p-6 justify-start text-left border border-border/50 bg-card/50 hover:bg-card hover:border-amber-500/50"
            >
                <div class="flex items-start gap-4 w-full">
                    <div
                        class="w-12 h-12 rounded-lg bg-amber-500/20 flex items-center justify-center text-amber-400 shrink-0"
                    >
                        <BookOpen size={24} />
                    </div>
                    <div>
                        <h2 class="text-xl font-semibold mb-1">
                            Compatibility Proxy
                        </h2>
                        <p class="text-muted-foreground font-normal">
                            Try Nocturne alongside your existing Nightscout -
                            "try before you buy"
                        </p>
                    </div>
                </div>
            </Button>
        </div>
    {:else if currentStep === 1 && setupType}
        <!-- Step 1: Configuration Screen -->
        <Button
            onclick={goBackToTypeSelection}
            variant="ghost"
            size="sm"
            class="mb-6 gap-1.5 text-muted-foreground hover:text-foreground transition-colors -ml-2"
        >
            <ChevronLeft size={18} />
            Back to setup types
        </Button>

        <div class="mb-10">
            <h1 class="text-4xl font-bold tracking-tight mb-3">{pageTitle}</h1>
            <p class="text-lg text-muted-foreground">{pageDescription}</p>
        </div>

        <div class="space-y-6">
            <!-- Nightscout Configuration (for migrate/proxy modes) -->
            {#if setupType !== "fresh"}
                <section
                    class="rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm overflow-hidden"
                >
                    <div
                        class="px-6 py-5 border-b border-border/40 bg-muted/30 flex items-center gap-3"
                    >
                        <div
                            class="w-10 h-10 rounded-lg bg-orange-500/15 flex items-center justify-center"
                        >
                            <Server class="w-5 h-5 text-orange-500" />
                        </div>
                        <div>
                            <h2 class="text-lg font-semibold">
                                {setupType === "migrate"
                                    ? "Source Nightscout Instance"
                                    : "Target Nightscout Instance"}
                            </h2>
                            <p class="text-sm text-muted-foreground">
                                {setupType === "migrate"
                                    ? "Your existing Nightscout instance to import data from"
                                    : "Your production Nightscout instance"}
                            </p>
                        </div>
                    </div>
                    <div class="p-6 space-y-5">
                        {#if setupType === "migrate"}
                            <!-- Migration Mode Selection -->
                            <div class="space-y-3">
                                <Label class="text-sm font-medium"
                                    >Migration Method</Label
                                >
                                <div class="grid grid-cols-2 gap-3">
                                    <button
                                        type="button"
                                        onclick={() =>
                                            (params.migrationMode = "Api")}
                                        class="p-4 rounded-lg border-2 transition-all text-left {params.migrationMode ===
                                        'Api'
                                            ? 'border-primary bg-primary/5'
                                            : 'border-border/60 hover:border-border'}"
                                    >
                                        <div class="font-medium mb-1">
                                            API Migration
                                        </div>
                                        <div
                                            class="text-xs text-muted-foreground"
                                        >
                                            Connect via Nightscout API
                                        </div>
                                    </button>
                                    <button
                                        type="button"
                                        onclick={() =>
                                            (params.migrationMode = "MongoDb")}
                                        class="p-4 rounded-lg border-2 transition-all text-left {params.migrationMode ===
                                        'MongoDb'
                                            ? 'border-primary bg-primary/5'
                                            : 'border-border/60 hover:border-border'}"
                                    >
                                        <div class="font-medium mb-1">
                                            Direct MongoDB
                                        </div>
                                        <div
                                            class="text-xs text-muted-foreground"
                                        >
                                            Connect directly to MongoDB
                                        </div>
                                    </button>
                                </div>
                            </div>
                        {/if}

                        {#if params.migrationMode === "Api" || setupType === "compatibility-proxy"}
                            <div class="space-y-2">
                                <Label
                                    for="nightscoutUrl"
                                    class="text-sm font-medium"
                                    >Nightscout URL</Label
                                >
                                <Input
                                    id="nightscoutUrl"
                                    type="url"
                                    bind:value={params.nightscoutUrl}
                                    placeholder="https://my-site.herokuapp.com"
                                    class="h-11"
                                />
                            </div>
                            <div class="space-y-2">
                                <Label
                                    for="nightscoutApiSecret"
                                    class="text-sm font-medium"
                                    >API Secret</Label
                                >
                                <Input
                                    id="nightscoutApiSecret"
                                    type="password"
                                    bind:value={params.nightscoutApiSecret}
                                    placeholder="Your API secret"
                                    class="h-11"
                                />
                            </div>

                            <!-- Test Connection Button -->
                            <div class="pt-2">
                                <Button
                                    variant="outline"
                                    onclick={testConnection}
                                    disabled={connectionTest.status ===
                                        "testing"}
                                    class="gap-2"
                                >
                                    {#if connectionTest.status === "testing"}
                                        <Loader2 class="w-4 h-4 animate-spin" />
                                        Testing...
                                    {:else}
                                        <Server class="w-4 h-4" />
                                        Test Connection
                                    {/if}
                                </Button>
                            </div>

                            <!-- Connection Result -->
                            {#if connectionTest.status === "success"}
                                <div
                                    class="p-4 rounded-lg bg-green-500/10 border border-green-500/30"
                                >
                                    <div class="flex items-start gap-3">
                                        <CheckCircle2
                                            class="w-5 h-5 text-green-500 shrink-0 mt-0.5"
                                        />
                                        <div class="space-y-2 flex-1">
                                            <div
                                                class="font-medium text-green-700 dark:text-green-400"
                                            >
                                                Connection Successful
                                            </div>
                                            <div
                                                class="text-sm space-y-1 text-muted-foreground"
                                            >
                                                <div>
                                                    <span class="font-medium"
                                                        >Site:</span
                                                    >
                                                    {connectionTest.data.name}
                                                </div>
                                                {#if connectionTest.data.version}
                                                    <div>
                                                        <span
                                                            class="font-medium"
                                                            >Version:</span
                                                        >
                                                        {connectionTest.data
                                                            .version}
                                                    </div>
                                                {/if}
                                                {#if connectionTest.data.units}
                                                    <div>
                                                        <span
                                                            class="font-medium"
                                                            >Units:</span
                                                        >
                                                        {connectionTest.data
                                                            .units === "mmol"
                                                            ? "mmol/L"
                                                            : "mg/dL"}
                                                    </div>
                                                {/if}
                                                {#if connectionTest.data.latestSgv}
                                                    <div
                                                        class="flex items-center gap-1.5 pt-1"
                                                    >
                                                        <Droplet
                                                            class="w-4 h-4 text-blue-500"
                                                        />
                                                        <span
                                                            class="font-medium"
                                                            >Latest reading:</span
                                                        >
                                                        {formatGlucose(
                                                            connectionTest.data
                                                                .latestSgv,
                                                            connectionTest.data
                                                                .units,
                                                        )}
                                                        {#if connectionTest.data.latestTime}
                                                            <span
                                                                class="text-xs"
                                                            >
                                                                ({connectionTest
                                                                    .data
                                                                    .latestTime})
                                                            </span>
                                                        {/if}
                                                    </div>
                                                {/if}
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            {:else if connectionTest.status === "error"}
                                <div
                                    class="p-4 rounded-lg bg-red-500/10 border border-red-500/30"
                                >
                                    <div class="flex items-start gap-3">
                                        <AlertCircle
                                            class="w-5 h-5 text-red-500 shrink-0 mt-0.5"
                                        />
                                        <div>
                                            <div
                                                class="font-medium text-red-700 dark:text-red-400"
                                            >
                                                Connection Failed
                                            </div>
                                            <div
                                                class="text-sm text-muted-foreground"
                                            >
                                                {connectionTest.error}
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            {/if}
                        {:else if params.migrationMode === "MongoDb"}
                            <!-- MongoDB Direct Connection -->
                            <div class="space-y-2">
                                <Label
                                    for="mongoConnectionString"
                                    class="text-sm font-medium"
                                    >MongoDB Connection String</Label
                                >
                                <Input
                                    id="mongoConnectionString"
                                    type="password"
                                    bind:value={params.mongoConnectionString}
                                    placeholder="mongodb+srv://user:pass@cluster.mongodb.net"
                                    class="h-11 font-mono text-sm"
                                />
                                <p class="text-xs text-muted-foreground">
                                    Your MongoDB Atlas or self-hosted connection
                                    string
                                </p>
                            </div>
                            <div class="space-y-2">
                                <Label
                                    for="mongoDatabaseName"
                                    class="text-sm font-medium"
                                    >Database Name</Label
                                >
                                <Input
                                    id="mongoDatabaseName"
                                    bind:value={params.mongoDatabaseName}
                                    placeholder="nightscout"
                                    class="h-11"
                                />
                                <p class="text-xs text-muted-foreground">
                                    Usually "nightscout" or your site name
                                </p>
                            </div>
                        {/if}
                    </div>
                </section>
            {/if}

            <!-- Database Configuration -->
            <section
                class="rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm overflow-hidden"
            >
                <div
                    class="px-6 py-5 border-b border-border/40 bg-muted/30 flex items-center gap-3"
                >
                    <div
                        class="w-10 h-10 rounded-lg bg-blue-500/15 flex items-center justify-center"
                    >
                        <Database class="w-5 h-5 text-blue-500" />
                    </div>
                    <div>
                        <h2 class="text-lg font-semibold">
                            Database Configuration
                        </h2>
                        <p class="text-sm text-muted-foreground">
                            Choose how to store your data
                        </p>
                    </div>
                </div>
                <div class="p-6 space-y-4">
                    <!-- Container Option -->
                    <button
                        type="button"
                        onclick={() => (params.useContainer = true)}
                        class="w-full text-left p-5 rounded-lg border-2 transition-all duration-200 {params.useContainer
                            ? 'border-primary bg-primary/5 shadow-sm'
                            : 'border-border/60 hover:border-border hover:bg-muted/30'}"
                    >
                        <div class="flex items-start gap-4">
                            <div
                                class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0 {params.useContainer
                                    ? 'bg-primary/15'
                                    : 'bg-muted'}"
                            >
                                <Cloud
                                    class="w-5 h-5 {params.useContainer
                                        ? 'text-primary'
                                        : 'text-muted-foreground'}"
                                />
                            </div>
                            <div class="flex-1 min-w-0">
                                <div class="flex items-center gap-2 mb-1">
                                    <span class="font-semibold"
                                        >Included PostgreSQL Container</span
                                    >
                                    <span
                                        class="px-2 py-0.5 text-xs font-medium rounded-full bg-green-500/15 text-green-600"
                                        >Recommended</span
                                    >
                                </div>
                                <p class="text-sm text-muted-foreground">
                                    We'll set up and manage everything for you
                                    automatically
                                </p>
                            </div>
                            {#if params.useContainer}
                                <div
                                    class="w-6 h-6 rounded-full bg-primary flex items-center justify-center shrink-0"
                                >
                                    <Check
                                        class="w-4 h-4 text-primary-foreground"
                                    />
                                </div>
                            {/if}
                        </div>
                    </button>

                    <!-- External DB Option -->
                    <button
                        type="button"
                        onclick={() => (params.useContainer = false)}
                        class="w-full text-left p-5 rounded-lg border-2 transition-all duration-200 {!params.useContainer
                            ? 'border-primary bg-primary/5 shadow-sm'
                            : 'border-border/60 hover:border-border hover:bg-muted/30'}"
                    >
                        <div class="flex items-start gap-4">
                            <div
                                class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0 {!params.useContainer
                                    ? 'bg-primary/15'
                                    : 'bg-muted'}"
                            >
                                <HardDrive
                                    class="w-5 h-5 {!params.useContainer
                                        ? 'text-primary'
                                        : 'text-muted-foreground'}"
                                />
                            </div>
                            <div class="flex-1 min-w-0">
                                <span class="font-semibold"
                                    >External PostgreSQL Database</span
                                >
                                <p class="text-sm text-muted-foreground mt-1">
                                    Connect to your own managed database
                                    instance
                                </p>
                            </div>
                            {#if !params.useContainer}
                                <div
                                    class="w-6 h-6 rounded-full bg-primary flex items-center justify-center shrink-0"
                                >
                                    <Check
                                        class="w-4 h-4 text-primary-foreground"
                                    />
                                </div>
                            {/if}
                        </div>
                    </button>

                    <!-- Connection String Input -->
                    {#if !params.useContainer}
                        <div class="pt-4 pl-14 space-y-2">
                            <Label
                                for="connectionString"
                                class="text-sm font-medium"
                                >Connection String</Label
                            >
                            <Input
                                id="connectionString"
                                bind:value={params.connectionString}
                                placeholder="Host=...;Port=5432;Database=..."
                                class="font-mono text-sm h-11"
                            />
                            <p class="text-xs text-muted-foreground">
                                PostgreSQL connection string in key=value format
                            </p>
                        </div>
                    {/if}

                    <!-- Detailed Logging Toggle (only for compatibility-proxy mode) -->
                    {#if setupType === "compatibility-proxy"}
                        <div
                            class="flex items-center justify-between p-4 rounded-lg bg-muted/30 border border-border/40 mt-4"
                        >
                            <div class="flex items-center gap-4">
                                <div
                                    class="w-10 h-10 rounded-lg bg-background flex items-center justify-center"
                                >
                                    <BookOpen
                                        class="w-5 h-5 text-muted-foreground"
                                    />
                                </div>
                                <div>
                                    <div class="font-medium">
                                        Detailed Logging
                                    </div>
                                    <div class="text-sm text-muted-foreground">
                                        Log detailed request/response
                                        comparisons for debugging
                                    </div>
                                </div>
                            </div>
                            <Switch
                                checked={params.enableDetailedLogging}
                                onCheckedChange={(v) =>
                                    (params.enableDetailedLogging = v)}
                            />
                        </div>
                    {/if}
                </div>
            </section>

            <!-- Optional Services -->
            <section
                class="rounded-xl border border-border/60 bg-card/50 backdrop-blur-sm overflow-hidden"
            >
                <div
                    class="px-6 py-5 border-b border-border/40 bg-muted/30 flex items-center gap-3"
                >
                    <div
                        class="w-10 h-10 rounded-lg bg-purple-500/15 flex items-center justify-center"
                    >
                        <RefreshCw class="w-5 h-5 text-purple-500" />
                    </div>
                    <div>
                        <h2 class="text-lg font-semibold">Optional Services</h2>
                        <p class="text-sm text-muted-foreground">
                            Additional features to enhance your setup
                        </p>
                    </div>
                </div>
                <div class="p-6 space-y-3">
                    <div
                        class="flex items-center justify-between p-4 rounded-lg bg-muted/30 border border-border/40"
                    >
                        <div class="flex items-center gap-4">
                            <div
                                class="w-10 h-10 rounded-lg bg-background flex items-center justify-center"
                            >
                                <RefreshCw
                                    class="w-5 h-5 text-muted-foreground"
                                />
                            </div>
                            <div>
                                <div class="font-medium">
                                    Watchtower Auto-Updates
                                </div>
                                <div class="text-sm text-muted-foreground">
                                    Automatically keep containers up to date
                                </div>
                            </div>
                        </div>
                        <Switch
                            checked={params.watchtower}
                            onCheckedChange={(v) => (params.watchtower = v)}
                        />
                    </div>

                    <div
                        class="flex items-center justify-between p-4 rounded-lg bg-muted/30 border border-border/40"
                    >
                        <div class="flex items-center gap-4">
                            <div
                                class="w-10 h-10 rounded-lg bg-background flex items-center justify-center"
                            >
                                <Server class="w-5 h-5 text-muted-foreground" />
                            </div>
                            <div>
                                <div class="font-medium">Aspire Dashboard</div>
                                <div class="text-sm text-muted-foreground">
                                    Telemetry visualization and monitoring UI
                                </div>
                            </div>
                        </div>
                        <Switch
                            checked={params.includeDashboard}
                            onCheckedChange={(v) =>
                                (params.includeDashboard = v)}
                        />
                    </div>

                    <div
                        class="flex items-center justify-between p-4 rounded-lg bg-muted/30 border border-border/40"
                    >
                        <div class="flex items-center gap-4">
                            <div
                                class="w-10 h-10 rounded-lg bg-background flex items-center justify-center"
                            >
                                <Database
                                    class="w-5 h-5 text-muted-foreground"
                                />
                            </div>
                            <div>
                                <div class="font-medium">
                                    Scalar API Documentation
                                </div>
                                <div class="text-sm text-muted-foreground">
                                    Interactive API reference documentation
                                </div>
                            </div>
                        </div>
                        <Switch
                            checked={params.includeScalar}
                            onCheckedChange={(v) => (params.includeScalar = v)}
                        />
                    </div>
                </div>
            </section>

            <!-- Continue Button -->
            <div class="flex justify-end pt-4 sticky bottom-8">
                <div
                    class="bg-background/80 backdrop-blur-sm p-4 rounded-xl border border-border/50 shadow-lg"
                >
                    <Button
                        onclick={goToConnectors}
                        size="lg"
                        class="gap-2 px-6 h-12 text-base font-medium shadow-lg shadow-primary/20 hover:shadow-primary/30 transition-shadow"
                    >
                        Continue to Connectors
                        <ChevronRight size={20} />
                    </Button>
                </div>
            </div>
        </div>
    {:else if currentStep === 2}
        <!-- Step 2: Connector Selection -->
        <Button
            onclick={goBackToConfig}
            variant="ghost"
            size="sm"
            class="mb-6 gap-1.5 text-muted-foreground hover:text-foreground transition-colors -ml-2"
        >
            <ChevronLeft size={18} />
            Back to configuration
        </Button>

        <h1 class="text-3xl font-bold mb-2">Select Connectors</h1>
        <p class="text-muted-foreground mb-8">
            Choose which data sources to enable
        </p>

        <svelte:boundary
            onerror={(e) => console.error("Connectors boundary error:", e)}
        >
            {#await connectorsQuery}
                <div class="flex justify-center py-12">
                    <div
                        class="animate-spin w-8 h-8 border-2 border-primary border-t-transparent rounded-full"
                    ></div>
                </div>
            {:then connectors}
                <ConnectorToggleGroup.Root
                    value={selectedConnectors}
                    onValueChange={handleConnectorChange}
                    class="grid md:grid-cols-2 gap-4 mb-8"
                >
                    {#each connectors as connector}
                        <ConnectorToggleGroup.Item
                            value={connector.type}
                            displayName={connector.displayName}
                            description={connector.description}
                            category={connector.category}
                            fields={connector.fields}
                            onConfigure={() => openConfig(connector)}
                        />
                    {/each}
                </ConnectorToggleGroup.Root>

                <div
                    class="flex items-center justify-between mt-8 sticky bottom-8 bg-background/80 backdrop-blur-sm p-4 rounded-xl border border-border/50 shadow-lg"
                >
                    <p class="text-sm text-muted-foreground font-medium pl-2">
                        {selectedConnectors.length} connector{selectedConnectors.length !==
                        1
                            ? "s"
                            : ""} selected
                    </p>
                    <Button
                        onclick={goToDownload}
                        class="gap-2 shadow-sm"
                        size="lg"
                    >
                        Review & Download
                        <ChevronRight size={20} />
                    </Button>
                </div>
            {:catch error}
                <div
                    class="p-4 rounded-lg bg-destructive/10 border border-destructive/50 text-destructive mb-6"
                >
                    <p class="font-medium">Could not load connectors</p>
                    <p class="text-sm opacity-80">{error.message}</p>
                </div>
            {/await}
        </svelte:boundary>
    {/if}
</div>

<ConnectorConfigDialog
    open={isConfigDialogOpen}
    connector={configuringConnector}
    initialValues={configuringConnector ? connectorConfigs[configuringConnector.type] ?? {} : {}}
    onOpenChange={(open) => {
        isConfigDialogOpen = open;
        if (!open) configuringConnector = null;
    }}
    onSave={handleSaveConfig}
/>
