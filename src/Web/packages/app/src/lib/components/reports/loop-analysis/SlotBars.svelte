<!--
  Bars over spans of the slot axis, from zero to the value either side of it. A comparison bar is a
  muted outline drawn over the same span.
-->
<script lang="ts" module>
  export interface SlotBar {
    /** First slot covered. */
    start: number;
    /** Slot after the last one covered. */
    end: number;
    value: number;
    sparse?: boolean;
  }
</script>

<script lang="ts">
  import { Rect, getChartContext } from "layerchart";

  interface Props {
    bars: SlotBar[];
    comparison?: SlotBar[];
    color: string;
    /** Space left between neighbouring bars, in pixels. */
    gap?: number;
  }

  let { bars, comparison = [], color, gap = 2 }: Props = $props();

  const ctx = getChartContext();
  const xScale = $derived(ctx.xScale);
  const yScale = $derived(ctx.yScale);

  function box(bar: SlotBar) {
    const x0 = xScale(bar.start) + gap / 2;
    const x1 = xScale(bar.end) - gap / 2;
    const top = yScale(Math.max(0, bar.value));
    const bottom = yScale(Math.min(0, bar.value));
    return { x: x0, width: Math.max(1, x1 - x0), y: top, height: Math.max(bar.value === 0 ? 0 : 1, bottom - top) };
  }
</script>

{#each bars as bar (bar.start)}
  {@const b = box(bar)}
  <Rect {...b} rx={1} fill={color} fillOpacity={bar.sparse ? 0.35 : 1} />
{/each}
{#each comparison as bar (`c${bar.start}`)}
  {@const b = box(bar)}
  <Rect
    {...b}
    rx={1}
    fill="none"
    stroke="var(--foreground)"
    strokeOpacity={0.85}
    strokeWidth={1.25}
    dashArray="3 2"
  />
{/each}
