<!--
  A value's spread for each hour of the day on the slot axis: the P25-P75 band as a box over the
  hour's twelve slots and the median across it. A sparse hour fades only its box's fill and keeps
  its outline and median at full strength; the comparison is a dashed outline of its box and median on a ring of the background.
-->
<script lang="ts">
  import { Line, Rect, getChartContext } from "layerchart";
  import type { LoopHealthHourValue } from "#lib/api/index.js";
  import { BAND_FILL, hourSpan, SPARSE_BAND_FILL } from "./loop-health-view";

  interface Props {
    hours: LoopHealthHourValue[];
    comparison?: LoopHealthHourValue[];
    color: string;
  }

  let { hours, comparison = [], color }: Props = $props();

  const ctx = getChartContext();
  const xScale = $derived(ctx.xScale);
  const yScale = $derived(ctx.yScale);
  const GAP = 2;

  function shape(hour: LoopHealthHourValue & { value: NonNullable<LoopHealthHourValue["value"]> }) {
    const { p25, p50, p75 } = hour.value;
    const span = hourSpan(hour.hour ?? 0);
    const x = xScale(span.start) + GAP / 2;
    const width = Math.max(1, xScale(span.end) - GAP / 2 - x);
    const median = p50 == null ? null : yScale(p50);
    if (p25 == null || p75 == null) return { x, width, box: null, median };
    const top = yScale(p75);
    return { x, width, box: { y: top, height: Math.max(1, yScale(p25) - top) }, median };
  }

  const drawn = (list: LoopHealthHourValue[]) =>
    list.filter((h): h is LoopHealthHourValue & { value: NonNullable<LoopHealthHourValue["value"]> } => h.value != null);
</script>

{#each drawn(hours) as hour (hour.hour)}
  {@const s = shape(hour)}
  {@const sparse = hour.value.sparse ?? true}
  {#if s.box}
    <Rect
      x={s.x}
      width={s.width}
      {...s.box}
      rx={1}
      fill={color}
      fillOpacity={sparse ? SPARSE_BAND_FILL : BAND_FILL}
      stroke={sparse ? color : "none"}
      strokeWidth={1}
      data-testid={sparse ? "sparse-hour" : "hour"}
    />
  {/if}
  {#if s.median != null}
    <Line x1={s.x} x2={s.x + s.width} y1={s.median} y2={s.median} stroke={color} strokeWidth={2} />
  {/if}
{/each}
{#each drawn(comparison) as hour (`c${hour.hour}`)}
  {@const s = shape(hour)}
  {#if s.box}
    <Rect x={s.x} width={s.width} {...s.box} rx={1} fill="none" stroke="var(--background)" strokeWidth={5.25} />
    <Rect
      x={s.x}
      width={s.width}
      {...s.box}
      rx={1}
      fill="none"
      stroke="var(--foreground)"
      strokeOpacity={0.85}
      strokeWidth={1.25}
      dashArray="3 2"
      data-testid="compared-hour"
    />
  {/if}
  {#if s.median != null}
    <Line x1={s.x} x2={s.x + s.width} y1={s.median} y2={s.median} stroke="var(--background)" strokeWidth={5.25} />
    <Line x1={s.x} x2={s.x + s.width} y1={s.median} y2={s.median} stroke="var(--foreground)" strokeWidth={1.25} dashArray="3 2" />
  {/if}
{/each}
