<script lang="ts">
  import { Chart, Svg, Bars, Text, Tooltip, Line } from "layerchart";
  import { scaleBand, scaleLinear } from "d3-scale";

  // Minimum percentage to render as filled bar (below this = outline only)

  // Minimum bar height in percentage points for tiny values
  const MIN_BAR_PERCENT = 5;

  interface TimeInRangePercentages {
    severeLow?: number;
    low?: number;
    target?: number;
    high?: number;
    severeHigh?: number;
  }

  interface Props {
    /** Pre-computed percentages - required to avoid reactive API calls */
    percentages?: TimeInRangePercentages;
    /** Thresholds for the glucose ranges in mg/dL */
    thresholds?: {
      severeLow: number;
      low: number;
      high: number;
      severeHigh: number;
    };
    /** Chart orientation - 'vertical' (default) or 'horizontal' */
    orientation?: "vertical" | "horizontal";
    /** Whether to show threshold labels (default: true for vertical, false for horizontal) */
    showThresholds?: boolean;
    /** Whether to show percentage labels (default: true) */
    showLabels?: boolean;
    /** Whether to show connector lines to labels (default: true for vertical) */
    showConnectors?: boolean;
    /** Compact mode - smaller text and tighter spacing */
    compact?: boolean;
  }

  let {
    percentages,
    thresholds = { severeLow: 54, low: 70, high: 180, severeHigh: 250 },
    orientation = "vertical",
    showThresholds,
    showLabels = true,
    showConnectors,
    compact = false,
  }: Props = $props();

  // Default showThresholds and showConnectors based on orientation
  const effectiveShowThresholds = $derived(showThresholds ?? orientation === "vertical");
  const effectiveShowConnectors = $derived(showConnectors ?? orientation === "vertical");

  // Range keys in stacking order (bottom to top for vertical, left to right for horizontal)
  const rangeKeys = [
    "severeLow",
    "low",
    "target",
    "high",
    "severeHigh",
  ] as const;
  type RangeKey = (typeof rangeKeys)[number];

  // Color mapping
  const colorMap: Record<RangeKey, string> = {
    severeLow: "var(--glucose-very-low)",
    low: "var(--glucose-low)",
    target: "var(--glucose-in-range)",
    high: "var(--glucose-high)",
    severeHigh: "var(--glucose-very-high)",
  };

  const labelMap: Record<RangeKey, string> = {
    severeLow: "Very Low",
    low: "Low",
    target: "In Range",
    high: "High",
    severeHigh: "Very High",
  };

  // Normalized percentages
  const pct = $derived({
    severeLow: percentages?.severeLow ?? 0,
    low: percentages?.low ?? 0,
    target: percentages?.target ?? 0,
    high: percentages?.high ?? 0,
    severeHigh: percentages?.severeHigh ?? 0,
  });

  // Transform data for stacked bar chart - one row per range with cumulative positions
  // Tiny values get minimum height and outline-only styling (only in vertical mode)
  const stackedData = $derived.by(() => {
    let cumulative = 0;
    return rangeKeys.map((key) => {
      const value = pct[key];
      // Only apply minimum bar size in vertical mode
      const isTiny = orientation === "vertical" && value > 0 && value < MIN_BAR_PERCENT;
      const displaySize = isTiny ? MIN_BAR_PERCENT : value;
      const start = cumulative;
      cumulative += displaySize;
      const color = colorMap[key];

      return {
        category: "TIR",
        range: key,
        value,
        start,
        end: cumulative,
        // For layerchart compatibility
        y0: start,
        y1: cumulative,
        x0: start,
        x1: cumulative,
        color,
        label: labelMap[key],
        isTiny,
      };
    });
  });

  // Summary data for labels
  const rangeData = $derived(
    rangeKeys.map((key) => ({
      key,
      value: pct[key],
      color: colorMap[key],
      label: labelMap[key],
    }))
  );

  // Total display size (may exceed 100 if tiny values are expanded in vertical mode)
  const totalDisplaySize = $derived(
    stackedData.length > 0 ? stackedData[stackedData.length - 1].end : 100
  );

  // Calculate positions for threshold labels (using display positions from stackedData)
  const thresholdPositions = $derived.by(() => {
    const thresholdValues = [
      thresholds.severeLow,
      thresholds.low,
      thresholds.high,
      thresholds.severeHigh,
    ];
    // Use the end of each segment (except the last) as the position
    return stackedData.slice(0, -1).map((segment, i) => ({
      key: segment.range,
      position: segment.end,
      threshold: thresholdValues[i],
    }));
  });

  // Padding based on orientation and options
  const chartPadding = $derived.by(() => {
    if (orientation === "horizontal") {
      return { top: 0, bottom: 0, left: 0, right: 0 };
    }
    return { top: 8, bottom: 8, left: 0, right: effectiveShowThresholds || showLabels ? 100 : 0 };
  });
</script>

<div class="relative h-full w-full">
  {#if orientation === "horizontal"}
    <!-- Horizontal stacked bar (simple CSS-based for better control) -->
    <div class="h-full flex rounded overflow-hidden">
      {#each stackedData as segment}
        {#if segment.value > 0}
          <div
            class="h-full transition-all duration-200"
            style="width: {segment.value}%; background-color: {segment.color};"
            title="{segment.label}: {segment.value.toFixed(1)}%"
          ></div>
        {/if}
      {/each}
    </div>
  {:else}
    <!-- Vertical stacked bar (layerchart-based) -->
    <Chart
      data={stackedData}
      x="category"
      xScale={scaleBand().paddingInner(0.4).paddingOuter(0.2)}
      y={["y0", "y1"]}
      yScale={scaleLinear()}
      yDomain={[0, totalDisplaySize]}
      c="range"
      cDomain={[...rangeKeys]}
      cRange={rangeKeys.map((k) => colorMap[k])}
      padding={chartPadding}
      tooltip={{ mode: "band" }}
    >
      {#snippet children({ context })}
        <Svg>
          <Bars rx={4} strokeWidth={2} stroke="inherit" />

          <!-- Threshold labels at boundaries (on the bar) -->
          {#if effectiveShowThresholds}
            {#each thresholdPositions as tp}
              {@const yPos =
                context.height -
                (tp.position / totalDisplaySize) * context.height}

              <Text
                x={context.width - 64}
                y={yPos}
                textAnchor="end"
                verticalAnchor="middle"
                class="fill-muted-foreground text-xs tabular-nums"
                value={`${tp.threshold}`}
              />
            {/each}
          {/if}

          <!-- Spline connectors from bar to percentage labels -->
          {#if effectiveShowConnectors && showLabels}
            {#each stackedData as segment}
              {@const midpoint = (segment.y0 + segment.y1) / 2}
              {@const yPos =
                context.height - (midpoint / totalDisplaySize) * context.height}

              <Line
                y1={yPos}
                x1={context.width - 12}
                x2={context.width - 82}
                y2={yPos}
                stroke={segment.color}
                strokeWidth={1}
                x="x"
                y="y"
                stroke-dasharray="2,2"
              />
            {/each}
          {/if}

          <!-- Percentage labels on the right side -->
          {#if showLabels}
            {#each stackedData as segment}
              {@const midpoint = (segment.y0 + segment.y1) / 2}
              {@const yPos =
                context.height - (midpoint / totalDisplaySize) * context.height}

              <Text
                x={context.width - 8}
                y={yPos}
                textAnchor="start"
                verticalAnchor="middle"
                class={[
                  "tabular-nums",
                  segment.range === "target"
                    ? compact ? "fill-foreground text-lg font-bold" : "fill-foreground text-2xl font-bold"
                    : compact ? "fill-muted-foreground text-xs" : "fill-muted-foreground text-sm",
                ].join(" ")}
                value={`${Math.round(segment.value)}%`}
              />
            {/each}
          {/if}
        </Svg>

        <!-- Tooltip -->
        <Tooltip.Root>
          {#snippet children({ data: _data })}
            <Tooltip.List>
              {#each rangeData.toReversed() as range}
                <Tooltip.Item
                  label={range.label}
                  format="percent"
                  value={range.value / 100}
                  color={range.color}
                />
              {/each}
            </Tooltip.List>
          {/snippet}
        </Tooltip.Root>
      {/snippet}
    </Chart>
  {/if}
</div>
