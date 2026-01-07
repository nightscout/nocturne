<script lang="ts">
    import { Button } from "$lib/components/ui/button";
    import * as Card from "$lib/components/ui/card";
    import { Plus, ArrowLeftRight, BookOpen } from "@lucide/svelte";
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
</div>
