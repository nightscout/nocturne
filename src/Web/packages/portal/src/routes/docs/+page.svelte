<script lang="ts">
    import { resolve } from "$app/paths";
    import Content from "../../content/docs/index.svx";
    import SupportNocturne from "$lib/components/docs/SupportNocturne.svelte";
    import { ArrowRight } from "@lucide/svelte";

    const GUIDES = [
        {
            href: resolve("/docs/getting-started"),
            title: "Quick Start",
            desc: "Get up and running with Nocturne in just a few minutes.",
        },
        {
            href: resolve("/docs/installation"),
            title: "Installation Guide",
            desc: "Step-by-step guides for deploying Nocturne with Docker Compose, Portainer, and more.",
        },
        {
            href: resolve("/docs/configuration"),
            title: "Configuration Guide",
            desc: "Learn how to configure connectors, settings, and customize your instance.",
        },
        {
            href: resolve("/scalar"),
            title: "API Reference",
            desc: "Interactive API documentation powered by Scalar. Explore endpoints, test requests, and integrate with Nocturne.",
        },
    ];
</script>

<div class="max-w-3xl">
    <Content />

    <ul class="not-prose m-0 p-0 list-none border-t border-border">
        {#each GUIDES as guide (guide.href)}
            <li class="border-b border-border">
                <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- guide.href is resolve()d in GUIDES -->
                <a href={guide.href} class="group flex items-start gap-4 py-5 no-underline text-inherit">
                    <div class="flex-1">
                        <h2 class="text-lg font-semibold text-foreground m-0 mb-1 group-hover:underline underline-offset-4">
                            {guide.title}
                        </h2>
                        <p class="text-sm text-muted-foreground m-0">{guide.desc}</p>
                    </div>
                    <ArrowRight class="size-5 mt-1 text-muted-foreground group-hover:text-foreground transition-colors" aria-hidden="true" />
                </a>
            </li>
        {/each}
    </ul>

    <SupportNocturne />

    <p class="mt-12 text-sm text-muted-foreground">
        The documentation is still growing. If a guide you need is missing,
        check back soon or contribute one on GitHub.
    </p>
</div>
