<script lang="ts">
  import ColorFocusRange from "./ColorFocusRange.svelte";
  import { getGlucoseHeatmapFill } from "$lib/utils/chart-colors";
  import { GLUCOSE_HEATMAP_LEGEND_STOPS } from "$lib/utils/chart-colors";
</script>

<div class="year-overview bg-card text-card-foreground p-4" data-testid="preview-card">
  <h1 class="mb-3 text-lg font-semibold">Average glucose — Year Overview</h1>
  <ColorFocusRange glucose units="mmol" stops={GLUCOSE_HEATMAP_LEGEND_STOPS} />
  <div class="mt-5 flex flex-wrap gap-3">
    {#each [40, 45, 50, 54, 63, 72] as value}
      <div class="text-center text-xs">
        <div
          class="mb-1 size-10 rounded-sm"
          style:background={getGlucoseHeatmapFill(value)}
          data-testid={`sample-${value}`}
        ></div>
        {(value / 18).toFixed(1)}
      </div>
    {/each}
    <div class="text-center text-xs">
      <div
        class="mb-1 size-10 rounded-sm"
        style:background="rgb(0 0 0 / 5%)"
        data-testid="empty-sample"
      ></div>
      No data
    </div>
  </div>
</div>
