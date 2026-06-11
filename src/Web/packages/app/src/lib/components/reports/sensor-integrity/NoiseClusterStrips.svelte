<script lang="ts">
  /**
   * Daily small-multiples of the glucose trace with noise-cluster bands. One row per local day;
   * clicking a row selects that day for the expanded detail view.
   */
  import { type DayBucket } from "./buckets";
  import NoiseDayChart from "./NoiseDayChart.svelte";

  interface Props {
    buckets: DayBucket[];
    thresholds?: { low: number; high: number };
    selectedDateMs?: number | null;
    onSelectDay?: (bucket: DayBucket) => void;
  }

  let {
    buckets,
    thresholds = { low: 70, high: 180 },
    selectedDateMs = null,
    onSelectDay,
  }: Props = $props();

  // Shared y-axis ceiling so every row is comparable; rounded up to a sensible bound.
  const yMax = $derived.by(() => {
    let max = 220;
    for (const b of buckets) {
      for (const p of b.points) if (p.y > max) max = p.y;
    }
    return Math.ceil(max / 20) * 20;
  });
</script>

<div class="space-y-1">
  {#each buckets as bucket (bucket.dateMs)}
    <button
      type="button"
      onclick={() => onSelectDay?.(bucket)}
      class="flex w-full items-center gap-3 rounded-md px-2 py-1 text-left transition-colors hover:bg-muted/50 {selectedDateMs ===
      bucket.dateMs
        ? 'bg-muted ring-1 ring-border'
        : ''}"
    >
      <div class="w-20 shrink-0 text-xs text-muted-foreground">{bucket.label}</div>
      <div class="h-12 flex-1">
        <NoiseDayChart {bucket} {thresholds} {yMax} />
      </div>
      <div class="w-16 shrink-0 text-right">
        {#if bucket.clusterCount > 0}
          <span class="text-xs font-medium text-foreground">{bucket.clusterCount}</span>
          <span class="text-[10px] text-muted-foreground">
            {bucket.clusterCount === 1 ? "window" : "windows"}
          </span>
        {:else}
          <span class="text-[10px] text-muted-foreground">—</span>
        {/if}
      </div>
    </button>
  {/each}
</div>
