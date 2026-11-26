<script lang="ts">
    import { onMount } from "svelte";
    import { resolve } from "$app/paths";
    import { RadioGroup, RadioGroupCard } from "@nocturne/ui/ui/radio-group";
    import { hosting, type Hosting } from "./hosting.svelte.js";

    interface Props {
        /**
         * The page is the docs for one path, so the picker links between the two pages and marks
         * this one. Without it, the picker filters the `HostingOnly` blocks of the page.
         */
        current?: Hosting;
    }

    let { current }: Props = $props();

    const OPTIONS = [
        {
            value: "managed",
            href: resolve("/docs/managed-service"),
            title: "Managed service",
            description: "A provider runs Nocturne for you. You sign up and use your site.",
        },
        {
            value: "self-host",
            href: resolve("/docs/installation"),
            title: "Self-host",
            description: "You run Nocturne on your own server, VPS or Raspberry Pi.",
        },
    ] satisfies { value: Hosting; href: string; title: string; description: string }[];

    const LABEL = "How do you run Nocturne?";
    const labelId = $props.id();

    // Before the page runs in a browser (the prerender, or no JavaScript), the options are links.
    onMount(() => hosting.start());

    function isHosting(value: string): value is Hosting {
        return OPTIONS.some((option) => option.value === value);
    }

    // @wc-ignore
    const LINK_CARD =
        "relative flex flex-col items-start gap-2 rounded-lg border-2 border-border p-4 text-left no-underline outline-none transition-colors hover:bg-accent/50 focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-[current=page]:border-primary aria-[current=page]:bg-accent/30";
</script>

{#snippet card(option: (typeof OPTIONS)[number])}
    <span class="text-sm font-semibold text-foreground">{option.title}</span>
    <span class="text-sm text-muted-foreground">{option.description}</span>
{/snippet}

<div class="not-prose mb-8">
    <p id={labelId} class="text-sm font-medium text-foreground mb-3">{LABEL}</p>
    {#if current || !hosting.started}
        <nav aria-labelledby={labelId} class="grid gap-3 sm:grid-cols-2">
            {#each OPTIONS as option (option.value)}
                <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- option.href is resolve()d in OPTIONS -->
                <a href={option.href}
                    aria-current={option.value === current ? "page" : undefined}
                    onclick={() => hosting.choose(option.value)}
                    class={LINK_CARD}
                >
                    {@render card(option)}
                </a>
            {/each}
        </nav>
    {:else}
        <RadioGroup
            value={hosting.current ?? ""}
            onValueChange={(value) => isHosting(value) && hosting.choose(value)}
            aria-labelledby={labelId}
            class="sm:grid-cols-2"
        >
            {#each OPTIONS as option (option.value)}
                <RadioGroupCard value={option.value}>
                    {@render card(option)}
                </RadioGroupCard>
            {/each}
        </RadioGroup>
        <p class="text-sm text-muted-foreground mt-3 mb-0">
            {#if hosting.current === "managed"}
                The page now shows only the steps for a managed service. See
                <a href={resolve("/docs/managed-service")} class="text-primary hover:underline">the providers</a>.
            {:else if hosting.current === "self-host"}
                The page now shows only the steps to self-host. See
                <a href={resolve("/docs/installation")} class="text-primary hover:underline">the installation guide</a>.
            {:else}
                Select one, and the page shows only the steps for it.
            {/if}
        </p>
    {/if}
</div>
