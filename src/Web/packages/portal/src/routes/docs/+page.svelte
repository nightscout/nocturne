<script lang="ts">
    import { Button } from "@nocturne/app/ui/button";
    import { ArrowRight, Book, Rocket, Download, Settings } from "@lucide/svelte";
</script>

<div class="max-w-3xl">
    <h1 class="text-4xl font-bold tracking-tight mb-4">Documentation</h1>
    <p class="text-lg text-muted-foreground mb-8">
        Learn how to set up, configure, and get the most out of Nocturne.
    </p>

    <div class="grid gap-4 not-prose">
        <a
            href="/docs/getting-started"
            class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-green-500/15 flex items-center justify-center shrink-0"
                >
                    <Rocket class="w-6 h-6 text-green-500" />
                </div>
                <div class="flex-1">
                    <h2
                        class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
                    >
                        Quick Start
                    </h2>
                    <p class="text-sm text-muted-foreground">
                        Get up and running with Nocturne in just a few minutes.
                    </p>
                </div>
                <ArrowRight
                    class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors"
                />
            </div>
        </a>

        <a
            href="/docs/installation"
            class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-blue-500/15 flex items-center justify-center shrink-0"
                >
                    <Download class="w-6 h-6 text-blue-500" />
                </div>
                <div class="flex-1">
                    <h2
                        class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
                    >
                        Installation Guide
                    </h2>
                    <p class="text-sm text-muted-foreground">
                        Detailed instructions for installing Nocturne on various platforms.
                    </p>
                </div>
                <ArrowRight
                    class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors"
                />
            </div>
        </a>

        <a
            href="/docs/configuration"
            class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-purple-500/15 flex items-center justify-center shrink-0"
                >
                    <Settings class="w-6 h-6 text-purple-500" />
                </div>
                <div class="flex-1">
                    <h2
                        class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
                    >
                        Configuration Guide
                    </h2>
                    <p class="text-sm text-muted-foreground">
                        Learn how to configure connectors, settings, and customize your instance.
                    </p>
                </div>
                <ArrowRight
                    class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors"
                />
            </div>
        </a>
    </div>

    <div class="mt-12 p-6 rounded-xl border border-amber-500/30 bg-amber-500/5">
        <div class="flex items-start gap-3">
            <Book class="w-5 h-5 text-amber-500 mt-0.5 shrink-0" />
            <div>
                <h3 class="font-semibold text-amber-700 dark:text-amber-400 mb-1">
                    Documentation In Progress
                </h3>
                <p class="text-sm text-muted-foreground">
                    We're actively working on expanding the documentation. Check back soon
                    for more guides and tutorials, or contribute on GitHub!
                </p>
            </div>
        </div>
    </div>
</div>
