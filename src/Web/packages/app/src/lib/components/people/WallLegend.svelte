<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { bg, bgLabel } from "#lib/utils/formatting.js";
  import { sharedThresholds } from "./wall-thresholds.js";

  interface Props {
    people: TenantOverviewItem[];
  }

  const { people }: Props = $props();

  const bounds = $derived(sharedThresholds(people));
  const swatch = "size-[0.75em] shrink-0 rounded-sm";
</script>

<div class="flex flex-wrap items-center gap-x-5 gap-y-1" data-testid="wall-legend">
  <span class="text-muted-foreground">Glucose, {bgLabel()}</span>
  <ul class="contents">
    <li class="flex items-center gap-1.5">
      <span class="{swatch} bg-glucose-very-low" aria-hidden="true"></span>
      <span class="font-medium">Very low</span>
      {#if bounds}<span class="text-muted-foreground tabular-nums">Below {bg(bounds.urgentLow)}</span>{/if}
    </li>
    <li class="flex items-center gap-1.5">
      <span class="{swatch} bg-glucose-low" aria-hidden="true"></span>
      <span class="font-medium">Low</span>
      {#if bounds}<span class="text-muted-foreground tabular-nums">{bg(bounds.urgentLow)} to {bg(bounds.low)}</span>{/if}
    </li>
    <li class="flex items-center gap-1.5">
      <span class="{swatch} bg-glucose-in-range" aria-hidden="true"></span>
      <span class="font-medium">In range</span>
      {#if bounds}<span class="text-muted-foreground tabular-nums">{bg(bounds.low)} to {bg(bounds.high)}</span>{/if}
    </li>
    <li class="flex items-center gap-1.5">
      <span class="{swatch} bg-glucose-high" aria-hidden="true"></span>
      <span class="font-medium">High</span>
      {#if bounds}<span class="text-muted-foreground tabular-nums">{bg(bounds.high)} to {bg(bounds.urgentHigh)}</span>{/if}
    </li>
    <li class="flex items-center gap-1.5">
      <span class="{swatch} bg-glucose-very-high" aria-hidden="true"></span>
      <span class="font-medium">Very high</span>
      {#if bounds}<span class="text-muted-foreground tabular-nums">Above {bg(bounds.urgentHigh)}</span>{/if}
    </li>
  </ul>
  {#if !bounds}
    <span class="text-muted-foreground">Ranges are each person's own.</span>
  {/if}
</div>
