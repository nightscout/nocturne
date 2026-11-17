<!--
  One prediction curve's error by horizon, as a slider over its horizons: one tab stop, left and
  right (with Home and End) move between horizons, and the horizon under the pointer or focus is
  read out as the slider's value and in a tooltip under its column. The tooltip carries the bias,
  MAE and n only; the context table carries the counts behind them.
-->
<script lang="ts">
  import { Key } from "@nocturne/ui/keys";
  import { Axis, Chart, Rule, Svg } from "layerchart";
  import type { PredictionCurve, PredictionCurveAccuracy, PredictionHorizonAccuracy } from "#lib/api/index.js";
  import { plural } from "#lib/utils/plural.js";
  import HorizonErrorMarks from "./HorizonErrorMarks.svelte";
  import { curveCaption, curveLabel, horizonLabel } from "./prediction-labels.svelte";

  interface Props {
    kind: PredictionCurve;
    /** The range's curve; null when only the comparison range carried it. */
    curve: PredictionCurveAccuracy | null;
    comparison?: PredictionCurveAccuracy | null;
    color: string;
    yDomain: [number, number];
    scale: (mgdl: number) => number;
    /** A signed glucose difference in display units. */
    formatError: (mgdl: number) => string;
    /** An unsigned glucose difference in display units. */
    formatMae: (mgdl: number) => string;
    unit: string;
  }

  let { kind, curve, comparison = null, color, yDomain, scale, formatError, formatMae, unit }: Props = $props();

  const horizons = $derived(curve?.horizons ?? []);
  const compared = $derived(comparison?.horizons ?? null);
  const axis = $derived(horizons.length > 0 ? horizons : (compared ?? []));
  const title = $derived(curveLabel(kind));
  const tooltipId = $derived(`prediction-tooltip-${kind}`);

  let hovered = $state<number | null>(null);
  let focused = $state(false);
  let current = $state(0);
  const shown = $derived(hovered ?? (focused ? current : null));

  // Left and right are written into the tooltip's position classes too.
  const padding = { top: 30, right: 8, bottom: 22, left: 40 };

  function indexAt(event: PointerEvent, element: HTMLElement): number | null {
    const box = element.getBoundingClientRect();
    const width = box.width - padding.left - padding.right;
    const fraction = (event.clientX - box.left - padding.left) / width;
    if (width <= 0 || fraction < 0 || fraction >= 1) return null;
    return Math.floor(fraction * axis.length);
  }

  function onkeydown(event: KeyboardEvent) {
    const last = axis.length - 1;
    const next =
      event.key === Key.ArrowRight || event.key === Key.ArrowUp
        ? Math.min(last, current + 1)
        : event.key === Key.ArrowLeft || event.key === Key.ArrowDown
          ? Math.max(0, current - 1)
          : event.key === Key.Home
            ? 0
            : event.key === Key.End
              ? last
              : null;
    if (next == null) return;
    current = next;
    event.preventDefault();
  }

  const at = (list: PredictionHorizonAccuracy[] | null, minutes: number | undefined) =>
    list?.find((h) => h.minutes === minutes);

  const predictions = (n: number | undefined) => plural(n ?? 0, ["# prediction", "# predictions"]);

  function describe(index: number): string {
    const minutes = axis[index]?.minutes;
    const h = at(horizons, minutes);
    if (!h?.error) return `${horizonLabel(minutes)}: no matched predictions`;
    return `${horizonLabel(minutes)}: bias ${formatError(h.error.mean ?? 0)} ${unit}, MAE ${formatMae(h.mae ?? 0)} ${unit}, ${predictions(h.error.n)}`;
  }

  const tickFormat = (v: number) => horizonLabel(axis[Math.floor(v)]?.minutes);
</script>

<div class="space-y-1">
  <div class="space-y-0.5 px-1">
    <h3 class="flex items-center gap-2 text-sm font-medium">
      <span class="inline-block h-0.5 w-3 rounded-full bg-(--curve)" style:--curve={color} aria-hidden="true"></span>
      {title}
    </h3>
    <p class="text-xs text-muted-foreground">{curveCaption(kind)}</p>
  </div>
  {#if !curve}
    <p class="px-1 text-xs text-muted-foreground">Only in the compared range</p>
  {/if}

  <div
    class="relative h-52 w-full touch-pan-y rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
    role="slider"
    tabindex="0"
    aria-label={title}
    aria-valuemin={0}
    aria-valuemax={Math.max(0, axis.length - 1)}
    aria-valuenow={current}
    aria-valuetext={describe(current)}
    aria-describedby={shown != null ? tooltipId : undefined}
    onpointermove={(event) => (hovered = indexAt(event, event.currentTarget))}
    onpointerleave={() => (hovered = null)}
    onfocus={() => (focused = true)}
    onblur={() => (focused = false)}
    {onkeydown}
  >
    <Chart data={[]} xDomain={[0, Math.max(1, axis.length)]} {yDomain} {padding}>
      <Svg>
        <Axis
          placement="left"
          ticks={4}
          grid={{ class: "stroke-border/60" }}
          tickLabelProps={{ class: "text-2xs fill-muted-foreground" }}
        />
        <Rule y={0} class="stroke-foreground/50" />
        <HorizonErrorMarks {axis} {horizons} comparison={compared} {color} {scale} {formatMae} selected={shown} />
        <Axis
          placement="bottom"
          rule={{ class: "stroke-border" }}
          ticks={axis.map((_, i) => i + 0.5)}
          format={tickFormat}
          tickLabelProps={{ class: "text-2xs fill-muted-foreground" }}
        />
      </Svg>
    </Chart>

    {#if shown != null && axis[shown]}
      {@const minutes = axis[shown].minutes}
      {@const h = at(horizons, minutes)}
      {@const c = at(compared, minutes)}
      <!-- Under its own column, flipped inward at either edge; the offsets are the plot padding. -->
      <div
        id={tooltipId}
        class="pointer-events-none absolute top-full z-10 mt-1 min-w-40 rounded-md border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-md {shown ===
        0
          ? 'left-[calc(40px_+_(100%_-_48px)_*_var(--tip-from))]'
          : shown === axis.length - 1
            ? 'right-[calc(8px_+_(100%_-_48px)_*_(1_-_var(--tip-to)))]'
            : 'left-[calc(40px_+_(100%_-_48px)_*_var(--tip-mid))] -translate-x-1/2'}"
        style:--tip-from={shown / axis.length}
        style:--tip-to={(shown + 1) / axis.length}
        style:--tip-mid={(shown + 0.5) / axis.length}
        role="tooltip"
      >
        <p class="mb-1 font-medium">{title}, {horizonLabel(minutes)}</p>
        <dl class="grid grid-cols-[auto_auto] gap-x-3 tabular-nums">
          {#if h?.error}
            <dt class="text-muted-foreground">Bias</dt>
            <dd class="text-right">{formatError(h.error.mean ?? 0)} {unit}</dd>
            <dt class="text-muted-foreground">MAE</dt>
            <dd class="text-right">{formatMae(h.mae ?? 0)} {unit}</dd>
            <dt class="text-muted-foreground">n</dt>
            <dd class="text-right">{predictions(h.error.n)}</dd>
          {:else}
            <dt class="col-span-2 text-muted-foreground">No matched predictions</dt>
          {/if}
          {#if compared}
            <dt class="text-muted-foreground">Compared bias</dt>
            <dd class="text-right">{c?.error ? `${formatError(c.error.mean ?? 0)} ${unit}` : "–"}</dd>
          {/if}
        </dl>
        {#if axis[shown].settingsSignal}
          <p class="mt-1 text-muted-foreground">A settings signal. At this horizon, the loop has not yet acted on the prediction.</p>
        {/if}
        {#if h?.error?.sparse}
          <p class="mt-1 text-muted-foreground">Too few days to show a pattern</p>
        {/if}
      </div>
    {/if}
  </div>
</div>
