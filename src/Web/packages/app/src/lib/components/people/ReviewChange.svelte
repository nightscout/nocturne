<script lang="ts">
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import { formatNumber } from "#lib/utils/formatting.js";
  import { displayChange } from "./review-format.js";
  import { changeLabel } from "./review-phrases.svelte.js";
  import type { ReviewDays } from "./review-lens.js";

  interface Props {
    change: number | null | undefined;
    days: ReviewDays;
  }

  const { change, days }: Props = $props();

  const shown = $derived(displayChange(change));
</script>

{#if shown}
  <span
    role="img"
    aria-label={changeLabel(shown, days)}
    class="inline-flex items-center gap-0.5 text-sm text-muted-foreground tabular-nums"
    data-testid="review-change"
  >
    {#if shown.direction === "up"}
      <ArrowUp class="size-3.5" aria-hidden="true" />
    {:else if shown.direction === "down"}
      <ArrowDown class="size-3.5" aria-hidden="true" />
    {/if}
    {#if shown.underOne}&lt;1{:else}{formatNumber(shown.points)}{/if}
  </span>
{:else}
  <span></span>
{/if}
