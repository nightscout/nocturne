<script lang="ts">
    import { ArrowUpRight } from "@lucide/svelte";
    import { LINKS } from "$lib/data/links";
    import { track } from "$lib/analytics";

    let { class: className = "" }: { class?: string } = $props();

    const TIERS = [
        {
            tier: "supporter",
            amount: "US$10",
            name: "Supporter",
            desc: "Covers the hosting behind the docs, the container registry, and the release pipeline.",
            href: LINKS.subscribe10,
        },
        {
            tier: "sustainer",
            amount: "US$20",
            name: "Sustainer",
            desc: "Adds test hardware: pumps, CGM transmitters, and phones the connectors are verified against.",
            href: LINKS.subscribe20,
        },
        {
            tier: "patron",
            amount: "US$50",
            name: "Patron",
            desc: "Funds sustained maintainer time on connectors, security updates, and support.",
            href: LINKS.subscribe50,
        },
    ];
</script>

<section class="not-prose mt-12 pt-8 border-t border-border/60 {className}">
    <h2 class="text-2xl font-bold mb-3">Support Nocturne</h2>
    <p class="text-muted-foreground mb-5">
        Nocturne is free and always will be. A monthly subscription to the
        Nightscout Foundation covers servers, test devices, and the maintenance
        that keeps self-hosting working.
    </p>

    <ul class="m-0 p-0 list-none border-t border-border">
        {#each TIERS as tier (tier.tier)}
            <li class="border-b border-border">
                <a
                    href={tier.href}
                    target="_blank"
                    rel="external noopener noreferrer"
                    onclick={() => track("Support Tier Click", { tier: tier.tier })}
                    class="group grid gap-1 py-4 no-underline text-inherit sm:grid-cols-[9rem_1fr_auto] sm:gap-6 sm:items-baseline"
                >
                    <span class="flex items-baseline gap-2 sm:flex-col sm:gap-0.5">
                        <span class="font-semibold text-foreground group-hover:underline underline-offset-4">{tier.name}</span>
                        <span class="text-sm text-muted-foreground tabular-nums">{tier.amount} / month</span>
                    </span>
                    <span class="text-sm text-muted-foreground leading-relaxed">{tier.desc}</span>
                    <span class="inline-flex items-center gap-1.5 text-sm font-semibold text-brand">
                        Subscribe
                        <ArrowUpRight class="size-4" aria-hidden="true" />
                    </span>
                </a>
            </li>
        {/each}
    </ul>

    <p class="mt-4 text-xs text-muted-foreground">
        Secure checkout via Stripe &middot; Cancel any time &middot; One-off
        donations go through the
        <a
            href={LINKS.donate}
            target="_blank"
            rel="external noopener noreferrer"
            onclick={() => track("Donate Click", { destination: "foundation" })}
            class="font-semibold hover:underline text-brand">Nightscout Foundation</a
        >.
    </p>
</section>
