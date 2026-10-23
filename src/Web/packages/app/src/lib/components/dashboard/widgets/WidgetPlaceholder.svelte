<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import WidgetStatSkeleton from "./WidgetStatSkeleton.svelte";
  import type { TopWidgetPlaceholder } from "../widget-registry";

  interface Props {
    shape: TopWidgetPlaceholder;
  }

  let { shape }: Props = $props();
</script>

<!-- Each shape repeats its widget's first-render lines with the same spacing; a skeleton bar
     stands in for a line of text at that text size's line height (text-xs h-4, text-sm h-5, text-xl h-7,
     text-2xl h-8). -->
<WidgetCard title="Loading">
  {#if shape === "current-glucose"}
    <!-- GlucoseValueIndicator's xl loading skeleton, inside an inline-flex root as the
         indicator's is, so the line box keeps the same descent below it. -->
    <div>
      <div class="inline-flex w-full">
        <div class="bg-muted h-28 flex-1 animate-pulse rounded-lg"></div>
      </div>
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
    <!-- EmptyState at its compact size: the artwork, then a text-sm title. -->
    <div class="flex flex-col items-center gap-2 px-4 py-3">
      <div class="bg-muted size-16 animate-pulse rounded"></div>
      <div class="bg-muted h-5 w-28 animate-pulse rounded"></div>
    </div>
  {:else}
    <WidgetStatSkeleton {shape} />
  {/if}
</WidgetCard>
