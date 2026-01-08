<script lang="ts">
    import { page } from "$app/state";
    import { Book, Rocket, Download, Settings, ChevronRight } from "@lucide/svelte";

    const navSections = [
        {
            title: "Getting Started",
            icon: Rocket,
            items: [
                { href: "/docs", label: "Overview" },
                { href: "/docs/getting-started", label: "Quick Start" },
            ],
        },
        {
            title: "Installation",
            icon: Download,
            items: [
                { href: "/docs/installation", label: "Installation Guide" },
            ],
        },
        {
            title: "Configuration",
            icon: Settings,
            items: [
                { href: "/docs/configuration", label: "Configuration Guide" },
            ],
        },
    ];

    const isActive = (href: string) => {
        return page.url.pathname === href;
    };

    const isSectionActive = (items: { href: string }[]) => {
        return items.some((item) => page.url.pathname === item.href);
    };
</script>

<nav class="space-y-6">
    {#each navSections as section}
        <div>
            <div
                class="flex items-center gap-2 text-sm font-semibold text-foreground mb-2"
            >
                <section.icon class="w-4 h-4" />
                {section.title}
            </div>
            <ul class="space-y-1 ml-6">
                {#each section.items as item}
                    <li>
                        <a
                            href={item.href}
                            class="flex items-center gap-2 py-1.5 text-sm transition-colors {isActive(item.href)
                                ? 'text-primary font-medium'
                                : 'text-muted-foreground hover:text-foreground'}"
                        >
                            {#if isActive(item.href)}
                                <ChevronRight class="w-3 h-3" />
                            {/if}
                            {item.label}
                        </a>
                    </li>
                {/each}
            </ul>
        </div>
    {/each}
</nav>
