<script lang="ts">
    import { page } from "$app/state";
    import { goto } from "$app/navigation";
    import { wizardStore } from "$lib/stores/wizard.svelte";

    // Get setup type from URL
    const setupType = $derived(
        (page.url.searchParams.get("type") as
            | "fresh"
            | "migrate"
            | "compatibility-proxy") || "fresh",
    );

    // Initialize store with setup type
    $effect(() => {
        wizardStore.setSetupType(setupType);
    });

    let nightscoutUrl = $state("");
    let nightscoutApiSecret = $state("");
    let useContainer = $state(true);
    let connectionString = $state("");
    let watchtower = $state(true);

    function handleContinue() {
        // Save to store
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

        goto("/connectors");
    }
</script>

<div class="max-w-2xl mx-auto">
    <div class="mb-8">
        <a
            href="/"
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
            Back to setup types
        </a>
    </div>

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
        <!-- Nightscout config for migrate/proxy modes -->
        {#if setupType !== "fresh"}
            <section class="p-6 rounded-xl border border-border/50 bg-card/50">
                <h2 class="text-lg font-semibold mb-4">
                    {setupType === "migrate"
                        ? "Source Nightscout Instance"
                        : "Target Nightscout Instance"}
                </h2>
                <p class="text-sm text-muted-foreground mb-4">
                    {#if setupType === "migrate"}
                        Your existing Nightscout instance to import data from
                    {:else}
                        Your production Nightscout instance that Nocturne will
                        forward writes to
                    {/if}
                </p>

                <div class="space-y-4">
                    <div>
                        <label
                            class="block text-sm font-medium mb-2"
                            for="nightscoutUrl">Nightscout URL</label
                        >
                        <input
                            id="nightscoutUrl"
                            type="url"
                            bind:value={nightscoutUrl}
                            placeholder="https://my-nightscout.herokuapp.com"
                            class="w-full px-4 py-2 rounded-lg bg-background border border-border focus:border-primary focus:outline-none focus:ring-1 focus:ring-primary transition-colors"
                        />
                    </div>
                    <div>
                        <label
                            class="block text-sm font-medium mb-2"
                            for="nightscoutApiSecret">API Secret</label
                        >
                        <input
                            id="nightscoutApiSecret"
                            type="password"
                            bind:value={nightscoutApiSecret}
                            placeholder="Your API secret"
                            class="w-full px-4 py-2 rounded-lg bg-background border border-border focus:border-primary focus:outline-none focus:ring-1 focus:ring-primary transition-colors"
                        />
                    </div>
                </div>
            </section>
        {/if}

        <!-- Database config -->
        <section class="p-6 rounded-xl border border-border/50 bg-card/50">
            <h2 class="text-lg font-semibold mb-4">Database Configuration</h2>

            <div class="space-y-4">
                <label
                    class="flex items-start gap-4 p-4 rounded-lg border border-border/50 hover:border-primary/50 cursor-pointer transition-colors"
                    class:border-primary={useContainer}
                >
                    <input
                        type="radio"
                        bind:group={useContainer}
                        value={true}
                        class="mt-1"
                    />
                    <div>
                        <div class="font-medium">
                            Use included PostgreSQL container
                        </div>
                        <div class="text-sm text-muted-foreground">
                            Recommended for most users - we'll handle the setup
                        </div>
                    </div>
                </label>

                <label
                    class="flex items-start gap-4 p-4 rounded-lg border border-border/50 hover:border-primary/50 cursor-pointer transition-colors"
                    class:border-primary={!useContainer}
                >
                    <input
                        type="radio"
                        bind:group={useContainer}
                        value={false}
                        class="mt-1"
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
                    <div class="mt-4">
                        <label
                            class="block text-sm font-medium mb-2"
                            for="connectionString">Connection String</label
                        >
                        <input
                            id="connectionString"
                            type="text"
                            bind:value={connectionString}
                            placeholder="Host=mydb.example.com;Port=5432;Username=nocturne;Password=...;Database=nocturne"
                            class="w-full px-4 py-2 rounded-lg bg-background border border-border focus:border-primary focus:outline-none focus:ring-1 focus:ring-primary transition-colors font-mono text-sm"
                        />
                    </div>
                {/if}
            </div>
        </section>

        <!-- Optional services -->
        <section class="p-6 rounded-xl border border-border/50 bg-card/50">
            <h2 class="text-lg font-semibold mb-4">Optional Services</h2>

            <label
                class="flex items-start gap-4 p-4 rounded-lg border border-border/50 hover:border-primary/50 cursor-pointer transition-colors"
                class:border-primary={watchtower}
            >
                <input type="checkbox" bind:checked={watchtower} class="mt-1" />
                <div>
                    <div class="font-medium">
                        Enable Watchtower auto-updates
                    </div>
                    <div class="text-sm text-muted-foreground">
                        Automatically update containers when new images are
                        available
                    </div>
                </div>
            </label>
        </section>

        <!-- Continue button -->
        <div class="flex justify-end">
            <button
                onclick={handleContinue}
                class="px-6 py-3 rounded-lg bg-primary text-primary-foreground font-medium hover:bg-primary/90 transition-colors flex items-center gap-2"
            >
                Continue to Connectors
                <svg
                    xmlns="http://www.w3.org/2000/svg"
                    width="20"
                    height="20"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"><path d="m9 18 6-6-6-6" /></svg
                >
            </button>
        </div>
    </div>
</div>
