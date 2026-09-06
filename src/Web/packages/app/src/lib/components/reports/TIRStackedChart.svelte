<script lang="ts" module>
  import type { GlucoseRange } from "$lib/components/charts/print/chart-print-patterns";

  type BandKey = "veryLow" | "low" | "target" | "high" | "veryHigh";

  interface BandMeta {
    key: BandKey;
    label: string;
    color: string;
    pattern: GlucoseRange;
  }

  // Stacking order: bottom-to-top in vertical mode, left-to-right in horizontal mode
  // (lowest glucose first). Labels render top-down (highest first).
  const BANDS_STACK_ORDER: BandMeta[] = [
    { key: "veryLow", label: "Very Low", color: "var(--glucose-very-low)", pattern: "very-low" },
    { key: "low", label: "Low", color: "var(--glucose-low)", pattern: "low" },
    { key: "target", label: "In Range", color: "var(--glucose-in-range)", pattern: "in-range" },
    { key: "high", label: "High", color: "var(--glucose-high)", pattern: "high" },
    { key: "veryHigh", label: "Very High", color: "var(--glucose-very-high)", pattern: "very-high" },
  ];
  const BANDS_TOP_DOWN = BANDS_STACK_ORDER.toReversed();
</script>

<script lang="ts">
  import { Chart, Svg, Bars, Bar, Text, Tooltip } from "layerchart";
  import { scaleBand, scaleLinear, type ScaleBand } from "d3-scale";
  import { bgRange } from "$lib/utils/formatting";
  import { glucosePatternClass } from "$lib/components/charts/print/chart-print-patterns";
  import type { TimeInRangePercentages } from "$api-clients";

  interface Props {
    /** Pre-computed percentages - required to avoid reactive API calls */
    percentages?: TimeInRangePercentages;
    /** Target range bounds in mg/dL for the caption below the chart */
    thresholds?: { low: number; high: number };
    /** Chart orientation - 'vertical' (default) or 'horizontal' */
    orientation?: "vertical" | "horizontal";
    /** Whether to show the "Target Range" caption below the chart (default: false) */
    showThresholds?: boolean;
    /** Whether to show band labels beside the bar (vertical mode only; default: true) */
    showLabels?: boolean;
    /** Compact mode - smaller text and tighter spacing */
    compact?: boolean;
    /**
     * Overlay marking where the tenant's personal target range falls, alongside the clinical
     * bands. Unlike tightTarget, a personal range isn't a subset of any single clinical band
     * (it can straddle boundaries), so it's drawn as two markers across the whole bar. Markers
     * are omitted when belowPercent/abovePercent are absent (e.g. time-of-day schedules, where
     * cumulative-time positions don't correspond to glucose boundaries on a value-sorted bar).
     */
    personalRange?: {
      /** % of time below the personal range (0-100); marker drawn at this cumulative offset */
      belowPercent?: number;
      /** % of time above the personal range (0-100); marker drawn at 100 minus this offset */
      abovePercent?: number;
      /** Caption shown under the chart, e.g. "Your range: 80-160 - 74% of time" */
      label: string;
    };
  }

  let {
    percentages,
    thresholds = { low: 70, high: 180 },
    orientation = "vertical",
    showThresholds = false,
    showLabels = true,
    compact = false,
    personalRange,
  }: Props = $props();

  const vertical = $derived(orientation === "vertical");

  const pct = $derived({
    veryLow: percentages?.veryLow ?? 0,
    low: percentages?.low ?? 0,
    target: percentages?.target ?? 0,
    tightTarget: percentages?.tightTarget ?? 0,
    high: percentages?.high ?? 0,
    veryHigh: percentages?.veryHigh ?? 0,
  });

  // Cumulative stacking positions in true percentage space (no minimum-size expansion:
  // 0% bands render nothing in the bar; their labels still print "0%").
  const stackedData = $derived.by(() => {
    let cumulative = 0;
    return BANDS_STACK_ORDER.map((band) => {
      const value = pct[band.key];
      const start = cumulative;
      cumulative += value;
      return {
        ...band,
        category: "TIR",
        value,
        start,
        end: cumulative,
      };
    }).filter((segment) => segment.value > 0);
  });

  // Tight-target (70-140 mg/dL) is a subset of target, not a sibling band, so it renders as a
  // centered inset within the target segment rather than its own stacked segment.
  const tightInset = $derived.by(() => {
    const targetSeg = stackedData.find((s) => s.key === "target");
    if (!targetSeg || pct.tightTarget <= 0) return null;
    const size = Math.min(pct.tightTarget / targetSeg.value, 1) * (targetSeg.end - targetSeg.start);
    const start = targetSeg.start + (targetSeg.end - targetSeg.start - size) / 2;
    return { start, end: start + size };
  });

  // Personal-range marker positions on the cumulative axis (from the start of the stack).
  const personalMarkers = $derived.by(() => {
    if (personalRange?.belowPercent === undefined || personalRange.abovePercent === undefined)
      return null;
    return [personalRange.belowPercent, 100 - personalRange.abovePercent];
  });

  const labelRows = $derived(
    BANDS_TOP_DOWN.map((band) => ({ ...band, value: pct[band.key] }))
  );
</script>

{#snippet marks(context: { xScale: unknown; yScale: unknown; width: number; height: number })}
  {@const bandScale = (vertical ? context.xScale : context.yScale) as ScaleBand<string>}
  {@const bandPos = bandScale("TIR") ?? 0}
  {@const bandSize = bandScale.bandwidth()}
  {@const valueScale = (vertical ? context.yScale : context.xScale) as (v: number) => number}
  <Svg>
          <Bars>
            <!-- Round only the outer ends of the stack: the first segment's outer edge and
                 the last segment's outer edge. stackedData is filtered to value > 0, so the
                 last entry is the last band with data (a 0% top band never steals the cap).
                 Internal segment boundaries stay square so neighbours meet flush. -->
            {#each stackedData as segment, i (segment.key)}
              <Bar
                data={segment}
                radius={vertical ? 4 : 2}
                rounded={stackedData.length === 1
                  ? "all"
                  : i === 0
                    ? vertical ? "bottom" : "left"
                    : i === stackedData.length - 1
                      ? vertical ? "top" : "right"
                      : "none"}
                fill={segment.color}
                class={glucosePatternClass(segment.pattern)}
              />
            {/each}
          </Bars>

          <!-- Tight-target inset: a centered sub-region within the target bar, not its own
               stacked segment (it's a subset of target, not a sibling range). -->
          {#if tightInset}
            {@const a = valueScale(tightInset.start)}
            {@const b = valueScale(tightInset.end)}
            <rect
              x={vertical ? bandPos : Math.min(a, b)}
              y={vertical ? Math.min(a, b) : bandPos}
              width={vertical ? bandSize : Math.abs(b - a)}
              height={vertical ? Math.abs(b - a) : bandSize}
              rx={2}
              class={["fill-[var(--glucose-tight-range)]", glucosePatternClass("tight-range")].join(" ")}
              data-testid="tight-range-inset"
            />
          {/if}

          <!-- Personal target range markers: dashed lines spanning the bar, since a personal
               range can straddle clinical band boundaries (unlike tightTarget). -->
          {#if personalMarkers}
            {@const lineMax = vertical ? context.height : context.width}
            {#each personalMarkers as marker, i (i)}
              {@const pos = Math.min(Math.max(valueScale(marker), 1), lineMax - 1)}
              <line
                x1={vertical ? bandPos - 4 : pos}
                x2={vertical ? bandPos + bandSize + 4 : pos}
                y1={vertical ? pos : bandPos - 2}
                y2={vertical ? pos : bandPos + bandSize + 2}
                stroke="var(--foreground)"
                stroke-width={2}
                stroke-dasharray="3 2"
                data-testid="personal-range-marker"
              />
            {/each}
          {/if}

          <!-- Evenly-spaced band labels beside the bar, highest range first; 0% bands
               print their 0 here even though they render no segment. -->
          {#if vertical && showLabels}
            {#each labelRows as row, i (row.key)}
              {@const y = ((i + 0.5) / labelRows.length) * context.height}
              <Text
                x={context.width + 10}
                {y}
                textAnchor="start"
                verticalAnchor="middle"
                class={[
                  "tabular-nums",
                  row.key === "target"
                    ? compact ? "fill-foreground text-base font-bold" : "fill-foreground text-xl font-bold"
                    : compact ? "fill-muted-foreground text-xs" : "fill-muted-foreground text-sm",
                ].join(" ")}
                value={`${Math.round(row.value)}% ${row.label}`}
              />
              {#if row.key === "target" && pct.tightTarget > 0 && !compact}
                <Text
                  x={context.width + 10}
                  y={y + 18}
                  textAnchor="start"
                  verticalAnchor="middle"
                  class="fill-muted-foreground text-xs"
                  value={`${Math.round(pct.tightTarget)}% in tight range`}
                />
              {/if}
            {/each}
          {/if}
  </Svg>

  <Tooltip.Root>
    {#snippet children({ data: _data })}
      <Tooltip.List>
        {#each labelRows as row (row.key)}
          <Tooltip.Item
            label={row.label}
            format="percent"
            value={row.value / 100}
            color={row.color}
          />
          {#if row.key === "target" && pct.tightTarget > 0}
            <Tooltip.Item
              label="Tight Range"
              format="percent"
              value={pct.tightTarget / 100}
              color="var(--glucose-tight-range)"
            />
          {/if}
        {/each}
      </Tooltip.List>
    {/snippet}
  </Tooltip.Root>
{/snippet}

<div class={vertical ? "flex h-full w-full flex-col" : "w-full"}>
  <div class={vertical ? "min-h-0 flex-1" : compact ? "h-4" : "h-6"}>
    {#if vertical}
      <Chart
        data={stackedData}
        x="category"
        xScale={scaleBand().paddingInner(0.4).paddingOuter(0.2)}
        y={["start", "end"]}
        yScale={scaleLinear()}
        yDomain={[0, 100]}
        c="key"
        cDomain={stackedData.map((s) => s.key)}
        cRange={stackedData.map((s) => s.color)}
        padding={{ top: 4, bottom: 4, left: 0, right: showLabels ? (compact ? 96 : 130) : 0 }}
        tooltipContext={{ mode: "band" }}
      >
        {#snippet children({ context })}
          {@render marks(context)}
        {/snippet}
      </Chart>
    {:else}
      <Chart
        data={stackedData}
        x={["start", "end"]}
        xScale={scaleLinear()}
        xDomain={[0, 100]}
        y="category"
        yScale={scaleBand()}
        c="key"
        cDomain={stackedData.map((s) => s.key)}
        cRange={stackedData.map((s) => s.color)}
        padding={{ top: 0, bottom: 0, left: 0, right: 0 }}
        tooltipContext={{ mode: "band" }}
      >
        {#snippet children({ context })}
          {@render marks(context)}
        {/snippet}
      </Chart>
    {/if}
  </div>

  {#if showThresholds || personalRange}
    <div class={["mt-2 shrink-0 text-center text-muted-foreground", compact ? "text-[10px]" : "text-xs"].join(" ")}>
      {#if showThresholds}
        <p><span class="font-semibold text-foreground">Target Range:</span> {bgRange(thresholds.low, thresholds.high)}</p>
      {/if}
      {#if personalRange}
        <p>{personalRange.label}</p>
      {/if}
    </div>
  {/if}
</div>
