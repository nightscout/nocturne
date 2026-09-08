<script lang="ts">
    import { page } from "$app/state";
    import { Rocket, Download, Settings, Shield, Share2, Bell, Bot, Code2, KeyRound, Activity, LayoutGrid, Package, Utensils, ChevronRight, ChevronDown } from "@lucide/svelte";
    import { DOCS_NAV_SECTIONS, type DocsSectionId } from "$lib/data/docs-nav";
    import { track } from "$lib/analytics";

    // Exhaustive by type: a new section in docs-nav.ts will not compile until it has an icon.
    const ICONS: Record<DocsSectionId, typeof Rocket> = {
        "getting-started": Rocket,
        installation: Download,
        authentication: Shield,
        sharing: Share2,
        food: Utensils,
        alerts: Bell,
        bots: Bot,
        configuration: Settings,
        observability: Activity,
        "windows-widget": LayoutGrid,
        "connecting-apps": KeyRound,
        sdks: Package,
        "api-reference": Code2,
    };

    const isActive = (href: string) => {
        return page.url.pathname === href;
    };

    const isSectionActive = (items: { href: string }[]) => {
        return items.some((item) => page.url.pathname === item.href);
    };
</script>

<nav class="space-y-6">
    {#each DOCS_NAV_SECTIONS as section (section.id)}
        {@const Icon = ICONS[section.id]}
        <div>
            <div
                class="flex items-center gap-2 text-sm font-semibold text-foreground mb-2"
            >
                <Icon class="w-4 h-4" />
                {section.title}
                {#if isSectionActive(section.items)}
                    <ChevronDown class="w-3 h-3 ml-auto" />
                {:else}
                    <ChevronRight class="w-3 h-3 ml-auto" />
                {/if}
            </div>
            <ul class="space-y-1 ml-6">
                {#each section.items as item (item.href)}
                    <li>
                        <a
                            href={item.href}
                            onclick={() => track("Docs Nav", { section: section.id })}
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
