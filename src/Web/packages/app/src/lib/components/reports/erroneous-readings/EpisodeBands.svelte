<script lang="ts">
  /** A band behind the glucose trace for each episode, so its extent shows on a whole day. */
  import { getChartContext } from "layerchart";
  import { getGlucoseChartContext } from "#lib/components/dashboard/glucose-chart/chart-context.svelte.js";

  let { spans }: { spans: { from: number; to: number }[] } = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  /** Half a five-minute reading each side, so the band covers its first and last points. */
  const PAD_MS = 2.5 * 60_000;
</script>

{#each spans as span (span.from)}
  {@const x0 = Math.max(0, chartCtx.xScale(span.from - PAD_MS))}
  {@const x1 = Math.min(chartCtx.width, chartCtx.xScale(span.to + PAD_MS))}
  <rect
    data-testid="episode-band"
    x={x0}
    y={ctx.layout.glucose.top}
    width={Math.max(1, x1 - x0)}
    height={ctx.layout.glucose.height}
    class="fill-muted-foreground/15"
  />
{/each}
