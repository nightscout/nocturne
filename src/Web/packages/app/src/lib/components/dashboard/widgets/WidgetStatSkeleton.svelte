<script lang="ts">
  interface Props {
    shape: "tir" | "tdd" | "summary";
  }

  let { shape }: Props = $props();
</script>

<!-- Each shape is as tall as its widget's stats once loaded, with a skeleton bar standing in
     for a line of text at that text size's line height (text-xs h-4, text-xl h-7, text-2xl h-8)
     and the widget's own spacing. -->
{#if shape === "tir"}
  <div class="bg-muted mb-2 h-32 animate-pulse rounded @2xl:h-6"></div>
  <div class="flex items-baseline justify-between gap-2">
    <div class="bg-muted h-7 w-28 animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-20 animate-pulse rounded"></div>
  </div>
  <div class="mt-1 flex justify-between">
    <div class="bg-muted h-4 w-16 animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-16 animate-pulse rounded"></div>
  </div>
{:else if shape === "tdd"}
  <div class="bg-muted h-7 w-20 animate-pulse rounded"></div>
  <div class="mt-1 space-y-0.5">
    <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-24 animate-pulse rounded"></div>
  </div>
{:else}
  <div class="space-y-2">
    <div class="bg-muted h-8 w-full animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-full animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-full animate-pulse rounded"></div>
    <div class="bg-muted h-4 w-full animate-pulse rounded"></div>
  </div>
{/if}
