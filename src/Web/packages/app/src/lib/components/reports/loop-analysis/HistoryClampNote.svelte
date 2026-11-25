<!--
  What the history clamp does to a Loop Analysis page, under the reports layout's notice that the
  viewer can see only recent history. The page adds what the clamp changes on it.
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import Clock from "@lucide/svelte/icons/clock";
  import { plural } from "#lib/utils/plural.js";

  interface Props {
    response: { historyClamped?: boolean; historyWindowHours?: number; rangeOutsideHistory?: boolean };
    children?: Snippet;
  }

  let { response, children }: Props = $props();
</script>

{#if response.historyClamped}
  <p class="flex items-start gap-2 text-sm text-muted-foreground" data-testid="loop-analysis-history-clamp">
    <Clock class="mt-0.5 size-4 shrink-0" />
    <span>
      {#if response.rangeOutsideHistory}
        {plural(response.historyWindowHours ?? 0, [
          "The range that you selected ends before the last hour. You can see only the last hour, so this page has no data for this range.",
          "The range that you selected ends before the last # hours. You can see only the last # hours, so this page has no data for this range.",
        ])}
      {:else}
        {plural(response.historyWindowHours ?? 0, [
          "This page uses only the data from the last hour in each range that you select.",
          "This page uses only the data from the last # hours in each range that you select.",
        ])}
        It does not compare ranges.
        {@render children?.()}
      {/if}
    </span>
  </p>
{/if}
