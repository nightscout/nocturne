<!--
  A share for each hour of the day as a bar over the hour's twelve slots, from zero. A sparse hour
  keeps its outline at full strength and fades only its fill, so it stays legible as a pale bar
  rather than vanishing; the comparison is a dashed outline over the same hour, on a ring of the
  background so it stays legible over a dark bar.
-->
<script lang="ts">
  import { Rect, getChartContext } from "layerchart";
  import { hourSpan, SPARSE_BAR_FILL, type HourBar } from "./loop-health-view";

  interface Props {
    bars: HourBar[];
    comparison?: HourBar[];
    color: string;
  }

  let { bars, comparison = [], color }: Props = $props();

  const ctx = getChartContext();
  const xScale = $derived(ctx.xScale);
  const yScale = $derived(ctx.yScale);
  const GAP = 2;

  function box(bar: HourBar) {
    const span = hourSpan(bar.hour);
    const x = xScale(span.start) + GAP / 2;
    const top = yScale(Math.max(0, bar.value));
    const bottom = yScale(0);
    return { x, width: Math.max(1, xScale(span.end) - GAP / 2 - x), y: top, height: Math.max(bar.value === 0 ? 0 : 1, bottom - top) };
  }
</script>

{#each bars as bar (bar.hour)}
  <Rect
    {...box(bar)}
    rx={1}
    fill={color}
    fillOpacity={bar.sparse ? SPARSE_BAR_FILL : 1}
    stroke={bar.sparse ? color : "none"}
    strokeWidth={1}
    data-testid={bar.sparse ? "sparse-hour" : "hour"}
  />
{/each}
{#each comparison as bar (`c${bar.hour}`)}
  <Rect {...box(bar)} rx={1} fill="none" stroke="var(--background)" strokeWidth={5.25} />
  <Rect
    {...box(bar)}
    rx={1}
    fill="none"
    stroke="var(--foreground)"
    strokeOpacity={0.85}
    strokeWidth={1.25}
    dashArray="3 2"
    data-testid="compared-hour"
  />
{/each}
