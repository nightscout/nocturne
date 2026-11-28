<script lang="ts">
    import { ConfirmationBackground } from "water-ui";
    import { Button } from "@nocturne/ui/ui/button";
    import ExternalLink from "@lucide/svelte/icons/external-link";
    import { DEMO_ENABLED, DEMO_WEB_URL } from "#lib/config.js";
    import { track } from "#lib/analytics.js";

    const cards = $derived([
        {
            title: "Try it as a person",
            description:
                "See the dashboard of one person with diabetes: glucose, treatments, reports and settings.",
            href: DEMO_WEB_URL,
            destination: "demo",
        },
        {
            title: "Try it as a clinic",
            description:
                "See the People page of a clinic that follows eleven people, with the ones that need attention flagged.",
            href: `${DEMO_WEB_URL}/api/v4/demo/session?as=clinic&redirect=/people`,
            destination: "demo-clinic",
        },
    ]);
</script>

<svelte:head>
    <title>Demo - Nocturne</title>
</svelte:head>

<div class="container mx-auto px-4 py-20">
    {#if DEMO_ENABLED && DEMO_WEB_URL}
        <div class="max-w-3xl mx-auto text-center">
            <div
                class="relative mx-auto mb-6 h-16 w-48 overflow-hidden dark:hidden"
                aria-hidden="true"
            >
                <ConfirmationBackground palette="moss" motion="auto" autoplay="once" />
            </div>
            <h1 class="text-3xl font-bold mb-4">Live demo</h1>
            <p class="text-muted-foreground mb-8">
                A full Nocturne instance you can explore and change: add treatments, edit
                settings, browse reports. It runs on made-up data, not anyone's real
                readings, and it resets on a schedule, so you can click anything.
            </p>

            <!--
                Each card opens in a new tab rather than an embedded frame. The demo signs
                visitors in with a session cookie and runs on a different domain to this site,
                so a browser will not send that cookie to a frame embedded here.
            -->
            <div class="grid gap-6 sm:grid-cols-2 text-left">
                {#each cards as card (card.destination)}
                    <div class="flex flex-col gap-4 rounded-lg border p-6">
                        <div class="space-y-2">
                            <h2 class="text-xl font-semibold">{card.title}</h2>
                            <p class="text-muted-foreground">{card.description}</p>
                        </div>
                        <Button
                            href={card.href}
                            target="_blank"
                            rel="noopener noreferrer"
                            onclick={() => track("Outbound Click", { destination: card.destination })}
                            size="cta"
                            class="mt-auto"
                        >
                            {card.title}
                            <ExternalLink class="size-4" />
                        </Button>
                    </div>
                {/each}
            </div>

            <p class="text-sm text-muted-foreground mt-6">
                Everyone shares the same demo instance, so you may see changes other
                visitors have made.
            </p>
        </div>
    {:else}
        <div class="max-w-md mx-auto text-center">
            <h1 class="text-2xl font-bold mb-4">Demo not available</h1>
            <p class="text-muted-foreground mb-6">
                The demo instance is not currently enabled.
            </p>
            <Button href="/" variant="outline">Back to home</Button>
        </div>
    {/if}
</div>
