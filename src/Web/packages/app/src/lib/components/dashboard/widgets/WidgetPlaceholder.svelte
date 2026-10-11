<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import type { TopWidgetPlaceholder } from "../widget-registry";

  interface Props {
    shape: TopWidgetPlaceholder;
  }

  let { shape }: Props = $props();
</script>

<!-- Each shape repeats its widget's first-render lines with the same spacing; a skeleton bar
     stands in for a line of text at that text size's line height (text-xs h-4, text-xl h-7,
     text-2xl h-8). -->
<WidgetCard title="Loading">
  {#if shape === "delta"}
    <div class="bg-muted h-7 w-20 animate-pulse rounded"></div>
    <div class="mt-2 border-t border-transparent pt-2">
      <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
    </div>
  {:else if shape === "stat"}
    <div class="bg-muted h-8 w-20 animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
  {:else if shape === "clock"}
    <div class="flex flex-col items-center">
      <div class="bg-muted h-8 w-24 animate-pulse rounded"></div>
      <div class="bg-muted mt-1 h-4 w-28 animate-pulse rounded"></div>
    </div>
  {:else if shape === "connection"}
    <!-- A Badge's box: a text-xs line inside py-0.5 and a 1px border. -->
    <div class="flex">
      <div class="bg-muted animate-pulse rounded-md border border-transparent py-0.5">
        <div class="h-4 w-20"></div>
      </div>
    </div>
    <div class="bg-muted mt-1 h-4 w-32 animate-pulse rounded"></div>
  {:else if shape === "empty-state"}
    <div class="flex flex-col items-center py-2">
      <div class="bg-muted mb-1 size-6 animate-pulse rounded"></div>
      <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
    </div>
  {:else if shape === "caption"}
    <div class="flex flex-col items-center py-4">
      <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
    </div>
  {:else}
    <div class="flex items-center justify-center py-4">
      <div class="h-4 w-4 animate-spin rounded-full border-2 border-primary border-t-transparent"></div>
    </div>
  {/if}
</WidgetCard>
