<script lang="ts">
    import { Heart, ArrowUpRight } from "@lucide/svelte";
    import { LINKS } from "$lib/data/links";

    // Matches the portal accent used on the get-involved page. Not the
    // --glucose-in-range token, which theme packs swap to green.
    const ACCENT = "oklch(0.6 0.118 184.704)";

    let { class: className = "" }: { class?: string } = $props();

    const TIERS = [
        {
            amount: "US$10",
            name: "Supporter",
            desc: "Covers the hosting behind the docs, the container registry, and the release pipeline.",
            href: LINKS.subscribe10,
            featured: false,
        },
        {
            amount: "US$20",
            name: "Sustainer",
            desc: "Adds test hardware — pumps, CGM transmitters, and phones the connectors are verified against.",
            href: LINKS.subscribe20,
            featured: true,
        },
        {
            amount: "US$50",
            name: "Patron",
            desc: "Funds sustained maintainer time on connectors, security updates, and support.",
            href: LINKS.subscribe50,
            featured: false,
        },
    ];
</script>

<section class="not-prose mt-12 pt-8 border-t border-border/60 {className}">
    <h2 class="text-2xl font-bold mb-3 flex items-center gap-2.5">
        <Heart class="w-5 h-5 shrink-0" color={ACCENT} aria-hidden="true" />
        Support Nocturne
    </h2>
    <p class="text-muted-foreground mb-5">
        Nocturne is free and always will be. A monthly subscription to the
        Nightscout Foundation covers servers, test devices, and the maintenance
        that keeps self-hosting working.
    </p>

    <div class="grid gap-4 sm:grid-cols-3">
        {#each TIERS as tier (tier.name)}
            <a
                href={tier.href}
                target="_blank"
                rel="noopener noreferrer"
                class="group flex flex-col p-5 rounded-xl border transition-colors {tier.featured
                    ? 'sn-featured'
                    : 'border-border/60 bg-card/50 hover:bg-card'}"
                style="--sn-accent: {ACCENT}"
            >
                <div class="flex items-baseline gap-1.5">
                    <span class="text-3xl font-bold tracking-tight tabular-nums"
                        >{tier.amount}</span
                    >
                    <span class="text-sm text-muted-foreground">/ month</span>
                </div>
                <div
                    class="mt-1 text-xs font-semibold tracking-[0.08em] uppercase"
                    style={tier.featured
                        ? `color: ${ACCENT}`
                        : "color: var(--muted-foreground)"}
                >
                    {tier.name}
                </div>
                <p class="mt-3 text-sm text-muted-foreground leading-relaxed flex-1">
                    {tier.desc}
                </p>
                <span
                    class="sn-cta mt-4 inline-flex items-center gap-1.5 text-sm font-semibold"
                    style="color: {ACCENT}"
                >
                    Subscribe
                    <ArrowUpRight class="w-4 h-4" aria-hidden="true" />
                </span>
            </a>
        {/each}
    </div>

    <p class="mt-4 text-xs text-muted-foreground">
        Secure checkout via Stripe &middot; Cancel any time &middot; One-off
        donations go through the
        <a
            href={LINKS.donate}
            target="_blank"
            rel="noopener noreferrer"
            class="font-semibold hover:underline"
            style="color: {ACCENT}">Nightscout Foundation</a
        >.
    </p>
</section>

<style>
    .sn-featured {
        border-color: color-mix(in oklch, var(--sn-accent), transparent 45%);
        background: color-mix(in oklch, var(--sn-accent), var(--card) 88%);
    }

    a:hover:not(.sn-featured) {
        border-color: color-mix(in oklch, var(--sn-accent), transparent 55%);
    }

    .sn-cta :global(svg) {
        transition: transform 0.15s;
    }

    a:hover .sn-cta :global(svg) {
        transform: translate(2px, -2px);
    }
</style>
