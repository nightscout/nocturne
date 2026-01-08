<script lang="ts">
    import { goto } from "$app/navigation";
    import { wizardStore } from "$lib/stores/wizard.svelte";
    import { Button } from "@nocturne/app/ui/button";
    import { Input } from "@nocturne/app/ui/input";
    import { Label } from "@nocturne/app/ui/label";
    import { Switch } from "@nocturne/app/ui/switch";
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
    import { z } from "zod";
    import { testNightscoutConnection } from "$lib/data/portal.remote";

    // Schema for the setup type URL parameter
    const SetupParamsSchema = z.object({
        type: z
            .enum(["fresh", "migrate", "compatibility-proxy"])
            .optional(),
    });

    const params = useSearchParams(SetupParamsSchema);

    // Reactive setup type from URL params - undefined means show type selection
    const setupType = $derived(params.type);

    // Track if user has selected a type (to show configuration)
    const hasSelectedType = $derived(setupType !== undefined);

    $effect(() => {
        if (setupType) {
            wizardStore.setSetupType(setupType);
        }
    });

    let nightscoutUrl = $state("");
    let nightscoutApiSecret = $state("");
    let useContainer = $state(true);
    let connectionString = $state("");
    let watchtower = $state(true);
    let includeDashboard = $state(true);
    let includeScalar = $state(true);

    // Connection test state - using a single state object for the query
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

    async function testConnection() {
        if (!nightscoutUrl) {
            connectionTest = {
                status: "error",
                error: "Please enter a Nightscout URL",
            };
            return;
        }

        connectionTest = { status: "testing" };

        try {
            const result = await testNightscoutConnection({
                url: nightscoutUrl,
                apiSecret: nightscoutApiSecret || undefined,
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

    function handleContinue() {
        wizardStore.setPostgres({
            useContainer,
            connectionString: useContainer ? undefined : connectionString,
        });
        wizardStore.setOptionalServices({
            watchtower,
            includeDashboard,
            includeScalar,
        });

        if (setupType === "migrate") {
            wizardStore.setMigration({ nightscoutUrl, nightscoutApiSecret });
        } else if (setupType === "compatibility-proxy") {
            wizardStore.setCompatibilityProxy({
                nightscoutUrl,
                nightscoutApiSecret,
            });
        }

        goto("/connectors");
    }

    const pageTitle = $derived(
        setupType === "fresh"
            ? "Fresh Install Setup"
            : setupType === "migrate"
              ? "Migrate from Nightscout"
              : "Compatibility Proxy Setup",
    );

    const pageDescription = $derived(
        setupType === "fresh"
            ? "Set up a new Nocturne instance from scratch"
            : setupType === "migrate"
              ? "Import your existing Nightscout data into Nocturne"
              : "Run Nocturne alongside your existing Nightscout",
    );

    // Format glucose value based on units
    function formatGlucose(sgv: number, units?: string): string {
        if (units === "mmol") {
            return (sgv / 18).toFixed(1) + " mmol/L";
        }
        return sgv + " mg/dL";
    }
</script>

<div class="max-w-3xl mx-auto pb-12">
    {#if !hasSelectedType}
        <!-- Type Selection Screen -->
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
                href="/setup?type=fresh"
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
                        <h2 class="text-xl font-semibold mb-1">Fresh Install</h2>
                        <p class="text-muted-foreground font-normal">
                            New Nocturne instance with no existing data
                        </p>
                    </div>
                </div>
            </Button>

            <Button
                href="/setup?type=migrate"
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
                href="/setup?type=compatibility-proxy"
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
                            Try Nocturne alongside your existing Nightscout - "try
                            before you buy"
                        </p>
                    </div>
                </div>
            </Button>
        </div>
    {:else}
        <!-- Configuration Screen -->
        <!-- Back Button -->
        <Button
            href="/setup"
            variant="ghost"
            size="sm"
            class="mb-6 gap-1.5 text-muted-foreground hover:text-foreground transition-colors -ml-2"
        >
            <ChevronLeft size={18} />
            Back to setup types
        </Button>

        <!-- Header Section -->
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
                    <div class="space-y-2">
                        <Label for="nightscoutUrl" class="text-sm font-medium"
                            >Nightscout URL</Label
                        >
                        <Input
                            id="nightscoutUrl"
                            type="url"
                            bind:value={nightscoutUrl}
                            placeholder="https://my-site.herokuapp.com"
                            class="h-11"
                        />
                    </div>
                    <div class="space-y-2">
                        <Label
                            for="nightscoutApiSecret"
                            class="text-sm font-medium">API Secret</Label
                        >
                        <Input
                            id="nightscoutApiSecret"
                            type="password"
                            bind:value={nightscoutApiSecret}
                            placeholder="Your API secret"
                            class="h-11"
                        />
                    </div>

                    <!-- Test Connection Button -->
                    <div class="pt-2">
                        <Button
                            variant="outline"
                            onclick={testConnection}
                            disabled={connectionTest.status === "testing"}
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
                                                <span class="font-medium"
                                                    >Version:</span
                                                >
                                                {connectionTest.data.version}
                                            </div>
                                        {/if}
                                        {#if connectionTest.data.units}
                                            <div>
                                                <span class="font-medium"
                                                    >Units:</span
                                                >
                                                {connectionTest.data.units ===
                                                "mmol"
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
                                                <span class="font-medium"
                                                    >Latest reading:</span
                                                >
                                                {formatGlucose(
                                                    connectionTest.data
                                                        .latestSgv,
                                                    connectionTest.data.units,
                                                )}
                                                {#if connectionTest.data.latestTime}
                                                    <span class="text-xs">
                                                        ({connectionTest.data
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
                                    <div class="text-sm text-muted-foreground">
                                        {connectionTest.error}
                                    </div>
                                </div>
                            </div>
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
                    onclick={() => (useContainer = true)}
                    class="w-full text-left p-5 rounded-lg border-2 transition-all duration-200 {useContainer
                        ? 'border-primary bg-primary/5 shadow-sm'
                        : 'border-border/60 hover:border-border hover:bg-muted/30'}"
                >
                    <div class="flex items-start gap-4">
                        <div
                            class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0 {useContainer
                                ? 'bg-primary/15'
                                : 'bg-muted'}"
                        >
                            <Cloud
                                class="w-5 h-5 {useContainer
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
                        {#if useContainer}
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
                    onclick={() => (useContainer = false)}
                    class="w-full text-left p-5 rounded-lg border-2 transition-all duration-200 {!useContainer
                        ? 'border-primary bg-primary/5 shadow-sm'
                        : 'border-border/60 hover:border-border hover:bg-muted/30'}"
                >
                    <div class="flex items-start gap-4">
                        <div
                            class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0 {!useContainer
                                ? 'bg-primary/15'
                                : 'bg-muted'}"
                        >
                            <HardDrive
                                class="w-5 h-5 {!useContainer
                                    ? 'text-primary'
                                    : 'text-muted-foreground'}"
                            />
                        </div>
                        <div class="flex-1 min-w-0">
                            <span class="font-semibold"
                                >External PostgreSQL Database</span
                            >
                            <p class="text-sm text-muted-foreground mt-1">
                                Connect to your own managed database instance
                            </p>
                        </div>
                        {#if !useContainer}
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
                {#if !useContainer}
                    <div class="pt-4 pl-14 space-y-2">
                        <Label
                            for="connectionString"
                            class="text-sm font-medium">Connection String</Label
                        >
                        <Input
                            id="connectionString"
                            bind:value={connectionString}
                            placeholder="Host=...;Port=5432;Database=..."
                            class="font-mono text-sm h-11"
                        />
                        <p class="text-xs text-muted-foreground">
                            PostgreSQL connection string in key=value format
                        </p>
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
                            <RefreshCw class="w-5 h-5 text-muted-foreground" />
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
                    <Switch bind:checked={watchtower} />
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
                    <Switch bind:checked={includeDashboard} />
                </div>

                <div
                    class="flex items-center justify-between p-4 rounded-lg bg-muted/30 border border-border/40"
                >
                    <div class="flex items-center gap-4">
                        <div
                            class="w-10 h-10 rounded-lg bg-background flex items-center justify-center"
                        >
                            <Database class="w-5 h-5 text-muted-foreground" />
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
                    <Switch bind:checked={includeScalar} />
                </div>
            </div>
        </section>

        <!-- Continue Button -->
        <div class="flex justify-end pt-4">
            <Button
                onclick={handleContinue}
                size="lg"
                class="gap-2 px-6 h-12 text-base font-medium shadow-lg shadow-primary/20 hover:shadow-primary/30 transition-shadow"
            >
                Continue to Connectors
                <ChevronRight size={20} />
            </Button>
        </div>
    </div>
    {/if}
</div>
