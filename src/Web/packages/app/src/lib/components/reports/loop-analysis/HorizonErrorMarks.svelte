<!--
  One curve's error at each horizon, horizon i spanning [i, i + 1) of the x axis: the P10-P90 and
  P25-P75 spread of the signed error, the bias across it in the foreground ink on a surface halo,
  and the MAE and n above. The comparison range is drawn over it as a dashed outline of its P10-P90
  spread and its bias.
-->
<script lang="ts">
  import { Line, Rect, getChartContext } from "layerchart";
  import type { PredictionHorizonAccuracy } from "#lib/api/index.js";

  interface Props {
    /** Every horizon on the axis, in order; only their minutes and settings flags are read. */
    axis: PredictionHorizonAccuracy[];
    horizons: PredictionHorizonAccuracy[];
    comparison?: PredictionHorizonAccuracy[] | null;
    color: string;
    /** Display-unit conversion applied to every value before it is drawn. */
    scale: (mgdl: number) => number;
    formatMae: (mgdl: number) => string;
    selected: number | null;
  }

  let { axis, horizons, comparison = null, color, scale, formatMae, selected }: Props = $props();

  const ctx = getChartContext();
  const xScale = $derived(ctx.xScale);
  const yScale = $derived(ctx.yScale);
  const plotTop = $derived(Math.min(...yScale.range()));
  const plotHeight = $derived(Math.abs(yScale.range()[0] - yScale.range()[1]));

  const SPARSE_OPACITY = 0.4;

  const indexOf = (h: PredictionHorizonAccuracy) => axis.findIndex((a) => a.minutes === h.minutes);

  function column(i: number, inset: number) {
    const x0 = xScale(i + inset);
    return { x: x0, width: Math.max(1, xScale(i + 1 - inset) - x0) };
  }

  function band(i: number, low: number | undefined, high: number | undefined, inset: number) {
    const top = yScale(scale(high ?? 0));
    return { ...column(i, inset), y: top, height: Math.max(1, yScale(scale(low ?? 0)) - top) };
  }
</script>

{#each axis as h, i (h.minutes)}
  {#if h.settingsSignal}
    <Rect {...column(i, 0.04)} y={plotTop} height={plotHeight} class="fill-muted/60" />
  {/if}
  {#if selected === i}
    <Rect {...column(i, 0.04)} y={plotTop} height={plotHeight} class="fill-foreground/5 stroke-foreground/30" dashArray="3 2" />
  {/if}
{/each}

{#each horizons as h (h.minutes)}
  {@const i = indexOf(h)}
  {#if h.error && i >= 0}
    {@const opacity = h.error.sparse ? SPARSE_OPACITY : 1}
    {@const bias = yScale(scale(h.error.mean ?? 0))}
    {@const outer = band(i, h.error.p10, h.error.p90, 0.22)}
    <Rect {...outer} rx={2} fill="color-mix(in oklab, {color} 28%, transparent)" fillOpacity={opacity} />
    <Rect {...band(i, h.error.p25, h.error.p75, 0.22)} rx={2} fill="color-mix(in oklab, {color} 55%, transparent)" fillOpacity={opacity} />
    <Line x1={outer.x - 3} x2={outer.x + outer.width + 3} y1={bias} y2={bias} class="stroke-card" strokeWidth={6} stroke-linecap="round" opacity={opacity} />
    <Line x1={outer.x - 3} x2={outer.x + outer.width + 3} y1={bias} y2={bias} class="stroke-foreground" strokeWidth={2} stroke-linecap="round" opacity={opacity} />
    <text x={outer.x + outer.width / 2} y={plotTop - 16} text-anchor="middle" class="fill-muted-foreground text-2xs tabular-nums">
      MAE {formatMae(h.mae ?? 0)}
    </text>
    <text x={outer.x + outer.width / 2} y={plotTop - 5} text-anchor="middle" class="fill-muted-foreground text-2xs tabular-nums">
      n {(h.error.n ?? 0).toLocaleString()}
    </text>
  {/if}
{/each}

{#each comparison ?? [] as h (`c${h.minutes}`)}
  {@const i = indexOf(h)}
  {#if h.error && i >= 0}
    {@const outer = band(i, h.error.p10, h.error.p90, 0.3)}
    {@const bias = yScale(scale(h.error.mean ?? 0))}
    <Rect {...outer} rx={2} fill="none" stroke="var(--foreground)" strokeOpacity={0.8} strokeWidth={1.25} dashArray="3 2" />
    <Line x1={outer.x} x2={outer.x + outer.width} y1={bias} y2={bias} stroke="var(--foreground)" strokeWidth={1.5} dashArray="3 2" />
  {/if}
{/each}
