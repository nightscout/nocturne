<script lang="ts">
    import { resolve } from "$app/paths";
    import { ArrowRight, Check } from "@lucide/svelte";
    import { Button } from "@nocturne/ui/ui/button";
    import ReportsDemo from "./ReportsDemo.svelte";
    import ConnectorsDemo from "./ConnectorsDemo.svelte";
    import AlarmsDemo from "./AlarmsDemo.svelte";
    import AuthDemo from "./AuthDemo.svelte";
    import { PILLARS } from "$lib/data/pillars";

    interface Props {
        demoHeight?: number;
        /** The landing page introduces the pillars and closes them with a call to action; /features has its own. */
        standalone?: boolean;
    }
    let { demoHeight = 400, standalone = true }: Props = $props();
</script>

<section class="max-w-[1200px] mx-auto px-6 border-t border-border overflow-x-clip">
    {#if standalone}
        <h2 class="text-section font-bold text-foreground m-0 pt-20 pb-14">
            Four things Nocturne is good at
        </h2>
    {/if}

    {#each PILLARS as p, i (p.n)}
        {@const flip = i % 2 === 1}
        <div class="py-16 grid gap-12 items-center {i > 0 || standalone ? 'border-t border-border' : ''}
                    {flip ? 'md:grid-cols-[1.05fr_1fr]' : 'md:grid-cols-[1fr_1.05fr]'}"
             style:--highlight={p.color}>

            <div class="flex flex-col gap-5 min-w-0 {flip ? 'md:order-2' : 'md:order-1'}">
                <svelte:element this={standalone ? "h3" : "h2"} class="text-headline font-bold text-foreground m-0 text-balance">
                    {p.title}
                </svelte:element>

                <p class="text-lead text-muted-foreground m-0 max-w-[520px]">{p.body}</p>

                <ul class="m-0 mt-1 p-0 list-none flex flex-col gap-3">
                    {#each p.bullets as b, bi (bi)}
                        <li class="flex items-start gap-3 text-base text-foreground/85">
                            <Check class="size-4 mt-1 shrink-0 text-highlight" aria-hidden="true" />
                            {b}
                        </li>
                    {/each}
                </ul>
            </div>

            <div class="min-w-0 {flip ? 'md:order-1' : 'md:order-2'}">
                {#if p.n === 1}
                    <ReportsDemo height={demoHeight} />
                {:else if p.n === 2}
                    <ConnectorsDemo height={demoHeight} />
                {:else if p.n === 3}
                    <AlarmsDemo height={Math.max(520, demoHeight + 120)} />
                {:else}
                    <AuthDemo height={demoHeight} />
                {/if}
            </div>
        </div>
    {/each}

    {#if standalone}
        <div class="border-t border-border py-12 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-6">
            <div>
                <div class="text-xl font-bold text-foreground">See it all running together</div>
                <div class="text-sm text-muted-foreground mt-1">Open the demo, or run your own copy in a few minutes.</div>
            </div>
            <div class="flex flex-wrap gap-3 shrink-0">
                <Button href={resolve("/docs/installation")} size="cta">
                    Get started <ArrowRight class="size-4" />
                </Button>
                <Button href={resolve("/features")} variant="outline" size="cta">
                    All features
                </Button>
            </div>
        </div>
    {/if}
</section>
