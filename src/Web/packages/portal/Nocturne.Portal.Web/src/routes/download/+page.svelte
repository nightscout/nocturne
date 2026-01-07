<script lang="ts">
    import { goto } from "$app/navigation";
    import { wizardStore } from "$lib/stores/wizard.svelte";
    import { generateConfig, type GenerateRequest } from "$lib/api/client";

    let generating = $state(false);
    let error = $state<string | null>(null);

    async function handleDownload() {
        generating = true;
        error = null;

        try {
            const request: GenerateRequest = {
                setupType: wizardStore.state.setupType,
                postgres: wizardStore.state.postgres,
                optionalServices: wizardStore.state.optionalServices,
                connectors: wizardStore.state.selectedConnectors.map(
                    (type) => ({
                        type,
                        config: wizardStore.state.connectorConfigs[type] || {},
                    }),
                ),
            };

            if (
                wizardStore.state.setupType === "migrate" &&
                wizardStore.state.migration
            ) {
                request.migration = wizardStore.state.migration;
            }

            if (
                wizardStore.state.setupType === "compatibility-proxy" &&
                wizardStore.state.compatibilityProxy
            ) {
                request.compatibilityProxy =
                    wizardStore.state.compatibilityProxy;
            }

            const blob = await generateConfig(request);

            // Download the file
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

    function getSetupTypeLabel(): string {
        switch (wizardStore.state.setupType) {
            case "fresh":
                return "Fresh Install";
            case "migrate":
                return "Migrate from Nightscout";
            case "compatibility-proxy":
                return "Compatibility Proxy";
            default:
                return wizardStore.state.setupType;
        }
    }
</script>

<div class="max-w-2xl mx-auto">
    <div class="mb-8">
        <a
            href="/connectors"
            class="text-muted-foreground hover:text-foreground transition-colors text-sm flex items-center gap-1"
        >
            <svg
                xmlns="http://www.w3.org/2000/svg"
                width="16"
                height="16"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"><path d="m15 18-6-6 6-6" /></svg
            >
            Back to connectors
        </a>
    </div>

    <h1 class="text-3xl font-bold mb-2">Review Configuration</h1>
    <p class="text-muted-foreground mb-8">
        Verify your settings and download deployment files
    </p>

    <div class="space-y-6 mb-8">
        <!-- Setup Summary -->
        <section class="p-6 rounded-xl border border-border/50 bg-card/50">
            <h2 class="text-lg font-semibold mb-4">Setup Type</h2>
            <div class="flex items-center gap-3">
                <div
                    class="w-10 h-10 rounded-lg bg-primary/20 flex items-center justify-center text-primary"
                >
                    <svg
                        xmlns="http://www.w3.org/2000/svg"
                        width="20"
                        height="20"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        stroke-width="2"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        ><path
                            d="M12 22c5.523 0 10-4.477 10-10S17.523 2 12 2 2 6.477 2 12s4.477 10 10 10z"
                        /><path d="m9 12 2 2 4-4" /></svg
                    >
                </div>
                <span class="font-medium">{getSetupTypeLabel()}</span>
            </div>
        </section>

        <!-- Database Summary -->
        <section class="p-6 rounded-xl border border-border/50 bg-card/50">
            <h2 class="text-lg font-semibold mb-4">Database</h2>
            <div class="text-muted-foreground">
                {#if wizardStore.state.postgres.useContainer}
                    <p>✓ Using included PostgreSQL container</p>
                {:else}
                    <p>✓ External database configured</p>
                {/if}
            </div>
        </section>

        <!-- Connectors Summary -->
        <section class="p-6 rounded-xl border border-border/50 bg-card/50">
            <h2 class="text-lg font-semibold mb-4">Connectors</h2>
            {#if wizardStore.state.selectedConnectors.length === 0}
                <p class="text-muted-foreground">No connectors selected</p>
            {:else}
                <ul class="space-y-2">
                    {#each wizardStore.state.selectedConnectors as connector}
                        <li
                            class="flex items-center gap-2 text-muted-foreground"
                        >
                            <svg
                                xmlns="http://www.w3.org/2000/svg"
                                width="16"
                                height="16"
                                viewBox="0 0 24 24"
                                fill="none"
                                stroke="currentColor"
                                stroke-width="2"
                                stroke-linecap="round"
                                stroke-linejoin="round"
                                class="text-green-400"
                                ><path d="M20 6 9 17l-5-5" /></svg
                            >
                            {connector}
                        </li>
                    {/each}
                </ul>
            {/if}
        </section>

        <!-- Optional Services Summary -->
        <section class="p-6 rounded-xl border border-border/50 bg-card/50">
            <h2 class="text-lg font-semibold mb-4">Optional Services</h2>
            <div class="text-muted-foreground">
                {#if wizardStore.state.optionalServices.watchtower}
                    <p>✓ Watchtower auto-updates enabled</p>
                {:else}
                    <p>○ Watchtower disabled</p>
                {/if}
            </div>
        </section>
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
        <button
            onclick={handleDownload}
            disabled={generating}
            class="px-8 py-4 rounded-xl bg-primary text-primary-foreground font-semibold hover:bg-primary/90 transition-colors flex items-center gap-3 text-lg disabled:opacity-50 disabled:cursor-not-allowed"
        >
            {#if generating}
                <div
                    class="animate-spin w-5 h-5 border-2 border-current border-t-transparent rounded-full"
                ></div>
                Generating...
            {:else}
                <svg
                    xmlns="http://www.w3.org/2000/svg"
                    width="24"
                    height="24"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    ><path
                        d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"
                    /><polyline points="7 10 12 15 17 10" /><line
                        x1="12"
                        x2="12"
                        y1="15"
                        y2="3"
                    /></svg
                >
                Download Configuration
            {/if}
        </button>
    </div>

    <div class="mt-8 p-6 rounded-xl bg-muted/30 border border-border/30">
        <h3 class="font-semibold mb-2">After downloading:</h3>
        <ol
            class="list-decimal list-inside space-y-2 text-muted-foreground text-sm"
        >
            <li>Extract the ZIP file to your server</li>
            <li>
                Fill in any empty values in <code class="text-primary"
                    >.env</code
                >
            </li>
            <li>Run <code class="text-primary">docker compose up -d</code></li>
            <li>
                Access Nocturne at <code class="text-primary"
                    >http://localhost:1337</code
                >
            </li>
        </ol>
    </div>
</div>
