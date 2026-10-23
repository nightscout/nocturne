<!--
  The filters the API applied, on the page, so a printed or shared report says what it covers
  without the Filters panel.
-->
<script lang="ts">
  import type { LoopAnalysisMasks } from "$lib/api";
  import Funnel from "@lucide/svelte/icons/funnel";
  import { MASK_KEYS, weekdayLabel } from "./loop-analysis-filter";
  import { maskLabels } from "./filter-labels.svelte";

  let { applied }: { applied: LoopAnalysisMasks | null | undefined } = $props();

  const days = $derived(applied?.days ?? []);
  const someDays = $derived(days.length > 0 && days.length < 7);
  const excluded = $derived.by(() => {
    const labels = maskLabels();
    return MASK_KEYS.filter((key) => applied?.[key]).map((key) => labels[key]);
  });
</script>

{#if applied && (someDays || applied.profileName || excluded.length > 0)}
  <p class="flex items-start gap-2 text-sm text-muted-foreground" data-testid="loop-analysis-applied-filters">
    <Funnel class="mt-0.5 size-4 shrink-0" />
    <span>
      {#if someDays}Days: {days.map(weekdayLabel).join(", ")}.{/if}
      {#if applied.profileName}Profile: {applied.profileName}.{/if}
      {#if excluded.length > 0}Time not used: {excluded.join(", ")}.{/if}
    </span>
  </p>
{/if}
