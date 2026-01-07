<script lang="ts">
    import { wizardStore } from "$lib/stores/wizard.svelte";
    import {
        generateConfig,
        type GenerateRequest,
    } from "$lib/data/portal.remote";
    import { Button } from "$lib/components/ui/button";
    import * as Card from "$lib/components/ui/card";
    import { ChevronLeft, Download, CheckCircle } from "@lucide/svelte";

    let generating = $state(false);
    let error = $state<string | null>(null);

    async function handleDownload() {
        generating = true;
        error = null;

        try {
            const request: GenerateRequest = {
                setupType: wizardStore.setupType,
                postgres: wizardStore.postgres,
                optionalServices: wizardStore.optionalServices,
                connectors: wizardStore.selectedConnectors.map((type) => ({
                    type,
                    config: wizardStore.connectorConfigs[type] || {},
                })),
                migration: wizardStore.migration,
                compatibilityProxy: wizardStore.compatibilityProxy,
            };

            const blob = await generateConfig(request);

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
        switch (wizardStore.setupType) {
            case "fresh":
                return "Fresh Install";
            case "migrate":
                return "Migrate from Nightscout";
            case "compatibility-proxy":
                return "Compatibility Proxy";
            default:
                return wizardStore.setupType;
        }
    }
</script>

<div class="max-w-2xl mx-auto">
    <Button
        href="/connectors"
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
                    <span class="font-medium">{getSetupTypeLabel()}</span>
                </div>
            </Card.Content>
        </Card.Root>

        <Card.Root>
            <Card.Header>
                <Card.Title>Database</Card.Title>
            </Card.Header>
            <Card.Content>
                <p class="text-muted-foreground">
                    {wizardStore.postgres.useContainer
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
                {#if wizardStore.selectedConnectors.length === 0}
                    <p class="text-muted-foreground">No connectors selected</p>
                {:else}
                    <ul class="space-y-2">
                        {#each wizardStore.selectedConnectors as connector}
                            <li
                                class="flex items-center gap-2 text-muted-foreground"
                            >
                                <CheckCircle size={16} class="text-green-400" />
                                {connector}
                            </li>
                        {/each}
                    </ul>
                {/if}
            </Card.Content>
        </Card.Root>

        <Card.Root>
            <Card.Header>
                <Card.Title>Optional Services</Card.Title>
            </Card.Header>
            <Card.Content>
                <p class="text-muted-foreground">
                    {wizardStore.optionalServices.watchtower
                        ? "✓ Watchtower auto-updates enabled"
                        : "○ Watchtower disabled"}
                </p>
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
