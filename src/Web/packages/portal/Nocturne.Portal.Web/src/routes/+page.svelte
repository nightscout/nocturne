<script lang="ts">
    import { goto } from "$app/navigation";
</script>

<div class="max-w-3xl mx-auto">
    <div class="text-center mb-12">
        <h1
            class="text-4xl font-bold mb-4 bg-gradient-to-r from-primary to-primary/60 bg-clip-text text-transparent"
        >
            Configure Your Nocturne Instance
        </h1>
        <p class="text-lg text-muted-foreground">
            Generate deployment files for self-hosted Nocturne in minutes
        </p>
    </div>

    <div class="grid gap-4">
        <button
            onclick={() => goto("/setup?type=fresh")}
            class="group p-6 rounded-xl border border-border/50 bg-card/50 hover:bg-card hover:border-primary/50 transition-all text-left"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-primary/20 flex items-center justify-center text-primary shrink-0"
                >
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
                        ><path d="M12 5v14" /><path d="M5 12h14" /></svg
                    >
                </div>
                <div>
                    <h2
                        class="text-xl font-semibold mb-1 group-hover:text-primary transition-colors"
                    >
                        Fresh Install
                    </h2>
                    <p class="text-muted-foreground">
                        New Nocturne instance with no existing data
                    </p>
                </div>
            </div>
        </button>

        <button
            onclick={() => goto("/setup?type=migrate")}
            class="group p-6 rounded-xl border border-border/50 bg-card/50 hover:bg-card hover:border-primary/50 transition-all text-left"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-blue-500/20 flex items-center justify-center text-blue-400 shrink-0"
                >
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
                        ><path d="M3 12h18" /><path d="m6 9 3 3-3 3" /><path
                            d="m21 9-3 3 3 3"
                        /></svg
                    >
                </div>
                <div>
                    <h2
                        class="text-xl font-semibold mb-1 group-hover:text-blue-400 transition-colors"
                    >
                        Migrate from Nightscout
                    </h2>
                    <p class="text-muted-foreground">
                        Import existing data from your Nightscout instance
                    </p>
                </div>
            </div>
        </button>

        <button
            onclick={() => goto("/setup?type=compatibility-proxy")}
            class="group p-6 rounded-xl border border-border/50 bg-card/50 hover:bg-card hover:border-primary/50 transition-all text-left"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-amber-500/20 flex items-center justify-center text-amber-400 shrink-0"
                >
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
                        ><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" /><path
                            d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"
                        /></svg
                    >
                </div>
                <div>
                    <h2
                        class="text-xl font-semibold mb-1 group-hover:text-amber-400 transition-colors"
                    >
                        Compatibility Proxy
                    </h2>
                    <p class="text-muted-foreground">
                        Try Nocturne alongside your existing Nightscout - "try
                        before you buy"
                    </p>
                </div>
            </div>
        </button>
    </div>
</div>
