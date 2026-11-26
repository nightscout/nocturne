<script lang="ts">
    import type { Snippet } from "svelte";
    import { hosting as choice, type Hosting } from "./hosting.svelte.js";

    interface Props {
        /** The path this content is for. It is hidden when the reader chose the other one. */
        hosting: Hosting;
        children: Snippet;
    }

    let { hosting, children }: Props = $props();
</script>

{#if choice.shows(hosting)}
    <div>
        {#if choice.current === null}
            <p class="not-prose text-xs font-semibold uppercase tracking-wide text-muted-foreground mt-8 mb-0">
                {#if hosting === "managed"}Managed service{:else}Self-host{/if}
            </p>
        {/if}
        {@render children()}
    </div>
{/if}
