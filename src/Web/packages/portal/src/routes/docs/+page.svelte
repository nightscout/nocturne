<script lang="ts">
    import { resolve } from "$app/paths";
    import Content from "../../content/docs/index.svx";
    import SupportNocturne from "$lib/components/docs/SupportNocturne.svelte";
    import {
        ArrowRight,
        Book,
        Rocket,
        Download,
        Settings,
        Code2,
    } from "@lucide/svelte";
</script>

<div class="max-w-3xl">
    <Content />

    <div class="grid gap-4 not-prose">
        <a
            href={resolve("/docs/getting-started")}
            class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-docs-quick-start/15 flex items-center justify-center shrink-0"
                >
                    <Rocket class="w-6 h-6 text-docs-quick-start" />
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
            href={resolve("/docs/installation")}
            class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-docs-install/15 flex items-center justify-center shrink-0"
                >
                    <Download class="w-6 h-6 text-docs-install" />
                </div>
                <div class="flex-1">
                    <h2
                        class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
                    >
                        Installation Guide
                    </h2>
                    <p class="text-sm text-muted-foreground">
                        Step-by-step guides for deploying Nocturne with Docker Compose,
                        Portainer, and more.
                    </p>
                </div>
                <ArrowRight
                    class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors"
                />
            </div>
        </a>

        <a
            href={resolve("/docs/configuration")}
            class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-docs-configure/15 flex items-center justify-center shrink-0"
                >
                    <Settings class="w-6 h-6 text-docs-configure" />
                </div>
                <div class="flex-1">
                    <h2
                        class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
                    >
                        Configuration Guide
                    </h2>
                    <p class="text-sm text-muted-foreground">
                        Learn how to configure connectors, settings, and
                        customize your instance.
                    </p>
                </div>
                <ArrowRight
                    class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors"
                />
            </div>
        </a>

        <a
            href={resolve("/scalar")}
            class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
        >
            <div class="flex items-start gap-4">
                <div
                    class="w-12 h-12 rounded-lg bg-docs-api/15 flex items-center justify-center shrink-0"
                >
                    <Code2 class="w-6 h-6 text-docs-api" />
                </div>
                <div class="flex-1">
                    <h2
                        class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
                    >
                        API Reference
                    </h2>
                    <p class="text-sm text-muted-foreground">
                        Interactive API documentation powered by Scalar. Explore
                        endpoints, test requests, and integrate with Nocturne.
                    </p>
                </div>
                <ArrowRight
                    class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors"
                />
            </div>
        </a>
    </div>

    <SupportNocturne />

    <div class="mt-12 p-6 rounded-xl border border-warning/30 bg-warning/5">
        <div class="flex items-start gap-3">
            <Book class="w-5 h-5 text-warning mt-0.5 shrink-0" />
            <div>
                <h3
                    class="font-semibold text-warning mb-1"
                >
                    Documentation In Progress
                </h3>
                <p class="text-sm text-muted-foreground">
                    We're actively working on expanding the documentation. Check
                    back soon for more guides and tutorials, or contribute on
                    GitHub!
                </p>
            </div>
        </div>
    </div>
</div>
