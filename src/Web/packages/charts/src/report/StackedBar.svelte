<!--
  One stacked bar of parts of a whole (time in each glucose range, by default out of 100%): the
  segments in the order given, lowest first, in their own colours and print textures, with a key
  that reads each part's value and label. Every word and colour is the caller's. An `inset` draws a
  part that lies inside one segment (the tight range inside in-range) as a centred region of it;
  `markers` draw dashed lines across the bar at cumulative positions (a personal target range).
-->
<script lang="ts" module>
  import type { GlucoseRange } from "../print/patterns.js";

  export interface StackedBarSegment {
    key: string;
    label: string;
    value: number;
    color: string;
    /** The print texture; none for a part with no glucose range. */
    pattern?: GlucoseRange | null;
  }

  export interface StackedBarInset {
    /** The segment it lies inside. */
    within: string;
    value: number;
    label: string;
    /** The key's words after the value, where they differ from the tooltip's label. */
    keyLabel?: string;
    color: string;
    pattern?: GlucoseRange | null;
  }
</script>

<script lang="ts">
  import { Bar, Bars, Chart, Line, Rect, Svg, Tooltip } from "layerchart";
  import { scaleBand, scaleLinear } from "d3-scale";
  import TextureSwatch from "../print/TextureSwatch.svelte";
  import { patternClass } from "../print/patterns.js";
  import { bandScale, valueScale } from "./bar-layout.js";

  interface Props {
    /** The parts, in stacking order: bottom to top when vertical, left to right when horizontal. */
    segments: StackedBarSegment[];
    orientation?: "vertical" | "horizontal";
    /** The whole the parts are out of. */
    max?: number;
    /** A part's value as the key and tooltip print it. */
    formatValue?: (value: number) => string;
    /** The key: beside the bar when vertical; when horizontal, under it on paper only unless keyOnScreen. */
    showLabels?: boolean;
    keyOnScreen?: boolean;
    compact?: boolean;
    /** The part the key prints larger (in range). */
    emphasis?: string | null;
    inset?: StackedBarInset | null;
    /** Cumulative positions, on the bar's own scale, of dashed lines across it. */
    markers?: number[] | null;
  }

  let {
    segments,
    orientation = "vertical",
    max = 100,
    formatValue = (value: number) => `${Math.round(value)}%`,
    showLabels = true,
    keyOnScreen = false,
    compact = false,
    emphasis = null,
    inset = null,
    markers = null,
  }: Props = $props();

  const vertical = $derived(orientation === "vertical");

  // Cumulative positions, no minimum size: a 0 part draws nothing, its key still prints it.
  const stacked = $derived.by(() => {
    let cumulative = 0;
    return segments
      .map((segment) => {
        const start = cumulative;
        cumulative += segment.value;
        return { ...segment, category: "bar", start, end: cumulative };
      })
      .filter((segment) => segment.value > 0);
  });

  const insetRange = $derived.by(() => {
    if (!inset || inset.value <= 0) return null;
    const host = stacked.find((s) => s.key === inset.within);
    if (!host) return null;
    const size = Math.min(inset.value / host.value, 1) * (host.end - host.start);
    const start = host.start + (host.end - host.start - size) / 2;
    return { start, end: start + size };
  });

  /** Highest first beside a vertical bar; in the bar's own order under a horizontal one. */
  const keyRows = $derived(vertical ? segments.toReversed() : segments);
  const domain = $derived<[number, number]>([0, Math.max(max, stacked.at(-1)?.end ?? 0)]);
</script>

{#snippet marks(context: { xScale: unknown; yScale: unknown; width: number; height: number })}
  {@const band = bandScale(vertical ? context.xScale : context.yScale)}
  {@const bandPos = band("bar") ?? 0}
  {@const bandSize = band.bandwidth()}
  {@const scale = valueScale(vertical ? context.yScale : context.xScale)}
  <Svg>
    <Bars>
      <!-- Only the stack's two outer ends are rounded, so neighbours meet flush. -->
      {#each stacked as segment, i (segment.key)}
        <Bar
          data={segment}
          radius={vertical ? 4 : 2}
          rounded={stacked.length === 1
            ? "all"
            : i === 0
              ? vertical ? "bottom" : "left"
              : i === stacked.length - 1
                ? vertical ? "top" : "right"
                : "none"}
          fill={segment.color}
          class={segment.pattern ? patternClass(segment.pattern) : undefined}
          data-testid="stacked-bar-segment"
          data-key={segment.key}
          data-color={segment.color}
        />
      {/each}
    </Bars>

    {#if insetRange && inset}
      {@const a = scale(insetRange.start)}
      {@const b = scale(insetRange.end)}
      <Rect
        x={vertical ? bandPos : Math.min(a, b)}
        y={vertical ? Math.min(a, b) : bandPos}
        width={vertical ? bandSize : Math.abs(b - a)}
        height={vertical ? Math.abs(b - a) : bandSize}
        rx={2}
        fill={inset.color}
        class={inset.pattern ? patternClass(inset.pattern) : undefined}
        data-testid="tight-range-inset"
      />
    {/if}

    {#if markers}
      {@const lineMax = vertical ? context.height : context.width}
      {#each markers as marker, i (i)}
        {@const pos = Math.min(Math.max(scale(marker), 1), lineMax - 1)}
        <Line
          x1={vertical ? bandPos - 4 : pos}
          x2={vertical ? bandPos + bandSize + 4 : pos}
          y1={vertical ? pos : bandPos - 2}
          y2={vertical ? pos : bandPos + bandSize + 2}
          stroke="var(--foreground)"
          strokeWidth={2}
          dashArray="3 2"
          data-testid="personal-range-marker"
        />
      {/each}
    {/if}
  </Svg>

  <Tooltip.Root>
      <Tooltip.List>
        {#each segments.toReversed() as segment (segment.key)}
          <Tooltip.Item label={segment.label} value={formatValue(segment.value)} color={segment.color} />
          {#if inset && segment.key === inset.within && inset.value > 0}
            <Tooltip.Item label={inset.label} value={formatValue(inset.value)} color={inset.color} />
          {/if}
        {/each}
      </Tooltip.List>
  </Tooltip.Root>
{/snippet}

{#snippet keyItems()}
  {#each keyRows as row (row.key)}
    {@const emphasised = row.key === emphasis}
    <li class="flex items-center gap-1.5" data-testid="stacked-bar-key">
      {#if row.pattern}
        <TextureSwatch texture={row.pattern} />
      {:else}
        <span class="inline-block size-2.5 rounded-sm bg-(--swatch)" style:--swatch={row.color}></span>
      {/if}
      <span
        class={!vertical
          ? emphasised ? "font-semibold text-foreground" : ""
          : emphasised
            ? compact ? "text-base font-bold text-foreground" : "text-xl font-bold text-foreground"
            : compact ? "text-xs" : "text-sm"}
      >
        {formatValue(row.value)} {row.label}
      </span>
    </li>
    <!-- A compact bar on screen names the inset in its tooltip; paper has only this key. -->
    {#if inset && row.key === inset.within && inset.value > 0}
      <li class={["items-center gap-1.5 text-xs", compact && (vertical || !keyOnScreen) ? "hidden print:flex" : "flex", vertical && "-mt-1 pl-5"]}>
        {#if inset.pattern}
          <TextureSwatch texture={inset.pattern} />
        {/if}
        <span>{formatValue(inset.value)} {inset.keyLabel ?? inset.label}</span>
      </li>
    {/if}
  {/each}
{/snippet}

{#if vertical}
  <div class="flex min-h-0 flex-1 gap-3" data-testid="stacked-bar">
    <div class="min-h-0 min-w-0 flex-1">
      <Chart
        data={stacked}
        x="category"
        xScale={scaleBand().paddingInner(0.4).paddingOuter(0.2)}
        y={["start", "end"]}
        yScale={scaleLinear()}
        yDomain={domain}
        c="key"
        cDomain={stacked.map((s) => s.key)}
        cRange={stacked.map((s) => s.color)}
        padding={{ top: 4, bottom: 4, left: 0, right: 0 }}
        tooltipContext={{ mode: "band" }}
      >
        {#snippet children({ context })}
          {@render marks(context)}
        {/snippet}
      </Chart>
    </div>
    {#if showLabels}
      <ul class="flex shrink-0 flex-col justify-around whitespace-nowrap tabular-nums text-muted-foreground">
        {@render keyItems()}
      </ul>
    {/if}
  </div>
{:else}
  <div class={compact ? "h-4" : "h-6"} data-testid="stacked-bar">
    <Chart
      data={stacked}
      x={["start", "end"]}
      xScale={scaleLinear()}
      xDomain={domain}
      y="category"
      yScale={scaleBand()}
      c="key"
      cDomain={stacked.map((s) => s.key)}
      cRange={stacked.map((s) => s.color)}
      padding={{ top: 0, bottom: 0, left: 0, right: 0 }}
      tooltipContext={{ mode: "band" }}
    >
      {#snippet children({ context })}
        {@render marks(context)}
      {/snippet}
    </Chart>
  </div>
  {#if showLabels}
    <ul
      class={[
        "flex-wrap items-center gap-x-4 gap-y-1 pt-2 text-xs tabular-nums text-muted-foreground",
        keyOnScreen ? "flex" : "hidden print:flex",
      ]}
      data-testid="band-key"
    >
      {@render keyItems()}
    </ul>
  {/if}
{/if}
