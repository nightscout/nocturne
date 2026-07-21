<script lang="ts">
    import { Heart, ArrowUpRight } from "@lucide/svelte";

    type Props = {
        /** Heading above the tiers. Pass null to render the tiers on their own. */
        title?: string | null;
        blurb?: string;
        class?: string;
    };

    let {
        title = "Support Nocturne",
        blurb = "Nocturne is free, open source, and run by volunteers under the Nightscout Foundation, a registered non-profit. A monthly subscription covers servers, test devices, and the maintenance that keeps self-hosting working.",
        class: className = "",
    }: Props = $props();

    const TIERS = [
        {
            amount: "$10",
            name: "Supporter",
            desc: "Covers the hosting behind the docs, the container registry, and the release pipeline.",
            href: "https://buy.stripe.com/14A9AV4Gm9Dh1ribpXgIo02",
            featured: false,
        },
        {
            amount: "$20",
            name: "Sustainer",
            desc: "Adds test hardware — pumps, CGM transmitters, and phones the connectors are verified against.",
            href: "https://buy.stripe.com/cNifZj4Gm3eT3zqeC9gIo01",
            featured: true,
        },
        {
            amount: "$50",
            name: "Patron",
            desc: "Funds sustained maintainer time on connectors, security updates, and support.",
            href: "https://buy.stripe.com/4gMcN78WC8zdda01PngIo00",
            featured: false,
        },
    ];
</script>

<section class="not-prose {className}">
    {#if title}
        <h2 class="text-2xl font-bold mb-3 flex items-center gap-2.5">
            <Heart class="w-5 h-5 text-primary shrink-0" />
            {title}
        </h2>
    {/if}
    <p class="text-muted-foreground mb-5">{blurb}</p>

    <div class="grid gap-4 sm:grid-cols-3">
        {#each TIERS as tier (tier.name)}
            <a
                href={tier.href}
                target="_blank"
                rel="noopener noreferrer"
                class="group flex flex-col p-5 rounded-xl border transition-colors {tier.featured
                    ? 'border-primary/40 bg-primary/5 hover:border-primary/60'
                    : 'border-border/60 bg-card/50 hover:bg-card hover:border-primary/30'}"
            >
                <div class="flex items-baseline gap-1.5">
                    <span class="text-3xl font-bold tracking-tight tabular-nums"
                        >{tier.amount}</span
                    >
                    <span class="text-sm text-muted-foreground">/ month</span>
                </div>
                <div
                    class="mt-1 text-xs font-semibold tracking-[0.08em] uppercase {tier.featured
                        ? 'text-primary'
                        : 'text-muted-foreground'}"
                >
                    {tier.name}
                </div>
                <p class="mt-3 text-sm text-muted-foreground leading-relaxed flex-1">
                    {tier.desc}
                </p>
                <span
                    class="mt-4 inline-flex items-center gap-1.5 text-sm font-semibold text-primary"
                >
                    Subscribe
                    <ArrowUpRight
                        class="w-4 h-4 transition-transform group-hover:translate-x-0.5 group-hover:-translate-y-0.5"
                    />
                </span>
            </a>
        {/each}
    </div>

    <p class="mt-4 text-xs text-muted-foreground">
        Secure checkout via Stripe &middot; Cancel any time &middot; One-off donations go
        through the
        <a
            href="https://www.nightscoutfoundation.org/donate"
            target="_blank"
            rel="noopener noreferrer"
            class="text-primary hover:underline">Nightscout Foundation</a
        >.
    </p>
</section>
