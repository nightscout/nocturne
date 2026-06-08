<script lang="ts">
    import { page } from "$app/state";
    import { Rocket, Download, Settings, Shield, Share2, Bell, Bot, Code2, KeyRound, Activity, ChevronRight, ChevronDown } from "@lucide/svelte";

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
                { href: "/docs/installation", label: "Overview" },
                { href: "/docs/installation/docker-compose", label: "Docker Compose" },
                { href: "/docs/installation/portainer", label: "Portainer" },
            ],
        },
        {
            title: "Authentication",
            icon: Shield,
            items: [
                { href: "/docs/authentication", label: "Overview" },
                { href: "/docs/authentication/google", label: "Sign in with Google" },
                { href: "/docs/authentication/github", label: "Sign in with GitHub" },
                { href: "/docs/authentication/oidc", label: "Generic OIDC" },
            ],
        },
        {
            title: "Sharing & Privacy",
            icon: Share2,
            items: [
                { href: "/docs/sharing", label: "Sharing your data" },
            ],
        },
        {
            title: "Alerts",
            icon: Bell,
            items: [
                { href: "/docs/alerts/email", label: "Email (Resend)" },
            ],
        },
        {
            title: "Chat Bots",
            icon: Bot,
            items: [
                { href: "/docs/bots", label: "Overview" },
                { href: "/docs/bots/discord", label: "Discord" },
                { href: "/docs/bots/slack", label: "Slack" },
                { href: "/docs/bots/telegram", label: "Telegram" },
                { href: "/docs/bots/whatsapp", label: "WhatsApp" },
            ],
        },
        {
            title: "Configuration",
            icon: Settings,
            items: [
                { href: "/docs/configuration", label: "Configuration Guide" },
            ],
        },
        {
            title: "Observability",
            icon: Activity,
            items: [
                { href: "/docs/observability", label: "OpenTelemetry" },
            ],
        },
        {
            title: "Connecting Apps",
            icon: KeyRound,
            items: [
                { href: "/docs/connecting-apps", label: "App authorization (PKCE)" },
                { href: "/docs/connecting-apps/device-flow", label: "Mobile & device flow" },
            ],
        },
        {
            title: "API Reference",
            icon: Code2,
            items: [
                { href: "/scalar", label: "Interactive API Docs" },
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
                {#if isSectionActive(section.items)}
                    <ChevronDown class="w-3 h-3 ml-auto" />
                {:else}
                    <ChevronRight class="w-3 h-3 ml-auto" />
                {/if}
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
