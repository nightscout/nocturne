<script lang="ts">
    import { page } from "$app/state";
    import { wizardStore } from "$lib/stores/wizard.svelte";
    import { Button } from "$lib/components/ui/button";
    import { Input } from "$lib/components/ui/input";
    import { Label } from "$lib/components/ui/label";
    import * as Card from "$lib/components/ui/card";
    import { Switch } from "$lib/components/ui/switch";
    import { ChevronLeft, ChevronRight } from "@lucide/svelte";

    const setupType = $derived(
        (page.url.searchParams.get("type") as
            | "fresh"
            | "migrate"
            | "compatibility-proxy") || "fresh",
    );

    $effect(() => {
        wizardStore.setSetupType(setupType);
    });

    let nightscoutUrl = $state("");
    let nightscoutApiSecret = $state("");
    let useContainer = $state(true);
    let connectionString = $state("");
    let watchtower = $state(true);

    function saveAndGetNextUrl(): string {
        wizardStore.setPostgres({
            useContainer,
            connectionString: useContainer ? undefined : connectionString,
        });
        wizardStore.setOptionalServices({ watchtower });

        if (setupType === "migrate") {
            wizardStore.setMigration({ nightscoutUrl, nightscoutApiSecret });
        } else if (setupType === "compatibility-proxy") {
            wizardStore.setCompatibilityProxy({
                nightscoutUrl,
                nightscoutApiSecret,
            });
        }

        return "/connectors";
    }

    const nextUrl = $derived(saveAndGetNextUrl());
</script>

<div class="max-w-2xl mx-auto">
    <Button
        href="/"
        variant="ghost"
        size="sm"
        class="mb-8 gap-1 text-muted-foreground"
    >
        <ChevronLeft size={16} />
        Back to setup types
    </Button>

    <h1 class="text-3xl font-bold mb-2">
        {#if setupType === "fresh"}
            Fresh Install Setup
        {:else if setupType === "migrate"}
            Migrate from Nightscout
        {:else}
            Compatibility Proxy Setup
        {/if}
    </h1>
    <p class="text-muted-foreground mb-8">Configure your deployment settings</p>

    <div class="space-y-8">
        {#if setupType !== "fresh"}
            <Card.Root>
                <Card.Header>
                    <Card.Title
                        >{setupType === "migrate"
                            ? "Source Nightscout Instance"
                            : "Target Nightscout Instance"}</Card.Title
                    >
                    <Card.Description>
                        {setupType === "migrate"
                            ? "Your existing Nightscout instance to import data from"
                            : "Your production Nightscout instance"}
                    </Card.Description>
                </Card.Header>
                <Card.Content class="space-y-4">
                    <div class="space-y-2">
                        <Label for="nightscoutUrl">Nightscout URL</Label>
                        <Input
                            id="nightscoutUrl"
                            type="url"
                            bind:value={nightscoutUrl}
                            placeholder="https://my-nightscout.herokuapp.com"
                        />
                    </div>
                    <div class="space-y-2">
                        <Label for="nightscoutApiSecret">API Secret</Label>
                        <Input
                            id="nightscoutApiSecret"
                            type="password"
                            bind:value={nightscoutApiSecret}
                            placeholder="Your API secret"
                        />
                    </div>
                </Card.Content>
            </Card.Root>
        {/if}

        <Card.Root>
            <Card.Header>
                <Card.Title>Database Configuration</Card.Title>
            </Card.Header>
            <Card.Content class="space-y-4">
                <label
                    class="flex items-center gap-4 p-4 rounded-lg border cursor-pointer transition-colors"
                    class:border-primary={useContainer}
                >
                    <input
                        type="radio"
                        bind:group={useContainer}
                        value={true}
                    />
                    <div>
                        <div class="font-medium">
                            Use included PostgreSQL container
                        </div>
                        <div class="text-sm text-muted-foreground">
                            Recommended - we'll handle the setup
                        </div>
                    </div>
                </label>

                <label
                    class="flex items-center gap-4 p-4 rounded-lg border cursor-pointer transition-colors"
                    class:border-primary={!useContainer}
                >
                    <input
                        type="radio"
                        bind:group={useContainer}
                        value={false}
                    />
                    <div>
                        <div class="font-medium">
                            Use external PostgreSQL database
                        </div>
                        <div class="text-sm text-muted-foreground">
                            Connect to your own managed database
                        </div>
                    </div>
                </label>

                {#if !useContainer}
                    <div class="space-y-2">
                        <Label for="connectionString">Connection String</Label>
                        <Input
                            id="connectionString"
                            bind:value={connectionString}
                            placeholder="Host=...;Port=5432;..."
                            class="font-mono text-sm"
                        />
                    </div>
                {/if}
            </Card.Content>
        </Card.Root>

        <Card.Root>
            <Card.Header>
                <Card.Title>Optional Services</Card.Title>
            </Card.Header>
            <Card.Content>
                <div class="flex items-center justify-between">
                    <div>
                        <div class="font-medium">Watchtower auto-updates</div>
                        <div class="text-sm text-muted-foreground">
                            Automatically update containers
                        </div>
                    </div>
                    <Switch bind:checked={watchtower} />
                </div>
            </Card.Content>
        </Card.Root>

        <div class="flex justify-end">
            <Button href={nextUrl} class="gap-2">
                Continue to Connectors
                <ChevronRight size={20} />
            </Button>
        </div>
    </div>
</div>
