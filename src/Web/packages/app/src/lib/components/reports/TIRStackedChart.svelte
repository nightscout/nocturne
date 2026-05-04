<script lang="ts">
  import { Chart, Svg, Bars, Bar, Spline, Text, Tooltip } from "layerchart";
  import { scaleBand, scaleLinear } from "d3-scale";

  // Minimum percentage to render as filled bar (below this = outline only)

  // Minimum bar height in percentage points for tiny values
  const MIN_BAR_PERCENT = 5;

  interface TimeInRangePercentages {
    veryLow?: number;
    low?: number;
    target?: number;
    high?: number;
    veryHigh?: number;
  }

  interface Props {
    /** Pre-computed percentages - required to avoid reactive API calls */
    percentages?: TimeInRangePercentages;
    /** Thresholds for the glucose ranges in mg/dL */
    thresholds?: {
      veryLow: number;
      low: number;
      high: number;
      veryHigh: number;
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
    thresholds = { veryLow: 54, low: 70, high: 180, veryHigh: 250 },
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
    "veryLow",
    "low",
    "target",
    "high",
    "veryHigh",
  ] as const;
  type RangeKey = (typeof rangeKeys)[number];

  // Color mapping
  const colorMap: Record<RangeKey, string> = {
    veryLow: "var(--glucose-very-low)",
    low: "var(--glucose-low)",
    target: "var(--glucose-in-range)",
    high: "var(--glucose-high)",
    veryHigh: "var(--glucose-very-high)",
  };

  const labelMap: Record<RangeKey, string> = {
    veryLow: "Very Low",
    low: "Low",
    target: "In Range",
    high: "High",
    veryHigh: "Very High",
  };

  // Normalized percentages
  const pct = $derived({
    veryLow: percentages?.veryLow ?? 0,
    low: percentages?.low ?? 0,
    target: percentages?.target ?? 0,
    high: percentages?.high ?? 0,
    veryHigh: percentages?.veryHigh ?? 0,
  });

  // Transform data for stacked bar chart - one row per range with cumulative positions.
  // In vertical mode, tiny (<MIN) and empty (0%) segments get a minimum visible height;
  // empty segments are rendered as a dotted outline to signal they aren't included.
  const stackedData = $derived.by(() => {
    let cumulative = 0;
    return rangeKeys.map((key) => {
      const value = pct[key];
      const isEmpty = orientation === "vertical" && value === 0;
      const isTiny = orientation === "vertical" && value > 0 && value < MIN_BAR_PERCENT;
      const displaySize = isEmpty || isTiny ? MIN_BAR_PERCENT : value;
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
        isEmpty,
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
      thresholds.veryLow,
      thresholds.low,
      thresholds.high,
      thresholds.veryHigh,
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

<div class={orientation === "horizontal" ? "relative w-full" : "relative h-full w-full"}>
  {#if orientation === "horizontal"}
    <!-- Horizontal stacked bar (simple CSS-based for better control) -->
    <div class={["flex rounded overflow-hidden", compact ? "h-4" : "h-6"].join(" ")}>
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
        {@const targetSegment = stackedData.find((s) => s.range === "target")}
        {@const targetMid = targetSegment
          ? (targetSegment.y0 + targetSegment.y1) / 2
          : totalDisplaySize / 2}
        {@const targetY =
          context.height - (targetMid / totalDisplaySize) * context.height}
        {@const labelOffset = Math.min(
          compact ? 18 : 24,
          context.height * 0.18,
        )}
        {@const labelYByRange = {
          veryLow: targetY + labelOffset * 2,
          low: targetY + labelOffset,
          target: targetY,
          high: targetY - labelOffset,
          veryHigh: targetY - labelOffset * 2,
        }}
        <Svg>
          <!-- Bar segments: filled for value > 0, dotted outline for 0% -->
          <Bars>
            {#each stackedData as segment (segment.range)}
              <Bar
                data={segment}
                radius={4}
                strokeWidth={2}
                stroke={segment.color}
                fill={segment.isEmpty ? "transparent" : segment.color}
                stroke-dasharray={segment.isEmpty ? "4 3" : undefined}
              />
            {/each}
          </Bars>

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

          <!-- Stepped connectors (right-down-right) from bar midpoint to offset label -->
          {#if effectiveShowConnectors && showLabels}
            {#each stackedData as segment (segment.range)}
              {@const midpoint = (segment.y0 + segment.y1) / 2}
              {@const segMidY =
                context.height - (midpoint / totalDisplaySize) * context.height}
              {@const labelY = labelYByRange[segment.range]}
              {@const segEdgeX = context.width - 12}
              {@const labelStubX = context.width - 82}
              {@const elbowX = (segEdgeX + labelStubX) / 2}
              <Spline
                pathData={`M ${segEdgeX} ${segMidY} L ${elbowX} ${segMidY} L ${elbowX} ${labelY} L ${labelStubX} ${labelY}`}
                fill="none"
                stroke={segment.color}
                strokeWidth={1}
                stroke-dasharray="2 2"
              />
            {/each}
          {/if}

          <!-- Percentage labels on the right side, offset toward center -->
          {#if showLabels}
            {#each stackedData as segment (segment.range)}
              {@const labelY = labelYByRange[segment.range]}

              <Text
                x={context.width - 8}
                y={labelY}
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
