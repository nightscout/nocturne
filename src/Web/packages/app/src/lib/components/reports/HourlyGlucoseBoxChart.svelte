<script lang="ts">
  import { Chart, Axis, Svg, Tooltip } from "layerchart";
  import { bgValue, bgLabel } from "$lib/utils/formatting";

  interface HourlyBoxPlotData {
    hour: number;
    min: number;
    q1: number;
    median: number;
    q3: number;
    max: number;
    outliers: number[];
  }

  interface Props {
    boxPlotData: HourlyBoxPlotData[];
  }

  let { boxPlotData }: Props = $props();

  // Transform data for LayerChart. Box-plot values are mg/dL; convert to the user's
  // display units so the plotted geometry, axis, and reference lines all share one scale.
  const chartData = $derived.by(() => {
    return boxPlotData.map((data) => {
      const min = bgValue(data.min);
      const q1 = bgValue(data.q1);
      const median = bgValue(data.median);
      const q3 = bgValue(data.q3);
      const max = bgValue(data.max);
      return {
        hour: data.hour,
        min,
        q1,
        median,
        q3,
        max,
        outliers: data.outliers.map(bgValue),
        // For box plot visualization
        lowerWhisker: min,
        upperWhisker: max,
        boxHeight: q3 - q1,
        boxCenter: (q1 + q3) / 2,
      };
    });
  });

  // Format hour for display
  function formatHour(hour: number): string {
    if (hour === 0) return "12 AM";
    if (hour < 12) return `${hour} AM`;
    if (hour === 12) return "12 PM";
    return `${hour - 12} PM`;
  }

  // Define Y domain based on data
  const yDomain: [number, number] = $derived.by(() => {
    // chartData is already in display units; keep the domain in the same units.
    const fallback: [number, number] = [0, bgValue(400)];
    if (chartData.length === 0) return fallback;

    const allValues = chartData
      .flatMap((d) => [d.min, d.max, ...d.outliers])
      .filter((v) => v > 0);

    if (allValues.length === 0) return fallback;

    const minVal = Math.min(...allValues);
    const maxVal = Math.max(...allValues);
    const padding = (maxVal - minVal) * 0.1;

    return [Math.max(0, minVal - padding), maxVal + padding];
  });

  // Box occupies 0.8 of an hour slot; whisker caps are ~1.5% of the plot width.
  const BOX_HALF_HOURS = 0.4;
  const CAP_WIDTH_FRACTION = 0.015;
</script>

<div class="w-full h-96">
  {#if chartData.length > 0}
    <Chart
      data={chartData}
      x="hour"
      y="median"
      {yDomain}
      xDomain={[0, 23]}
      padding={{ top: 20, right: 30, bottom: 60, left: 60 }}
      tooltipContext={{ mode: "bisect-x" }}
    >
      {#snippet children({ context })}
        {@const boxHalfWidth = Math.abs(
          context.xScale(BOX_HALF_HOURS) - context.xScale(0),
        )}
        {@const capHalfWidth = context.width * CAP_WIDTH_FRACTION}
        <Svg>
          <!-- Y-axis with glucose threshold reference -->
          <Axis placement="left" rule grid label={`Glucose (${bgLabel()})`} />
          <Axis
            placement="bottom"
            rule
            label="Hour of Day"
            format={formatHour}
            ticks={[0, 3, 6, 9, 12, 15, 18, 21]}
          />

          <!-- Target range + threshold lines. Native SVG positioned through the
               chart scales: layerchart marks each call registerMark() on mount and
               every registration re-runs the chart's mark deriveds over all marks,
               so the box-plot geometry (7 marks x 24 hours) cost O(N^2). Native
               elements register nothing. (The prior <Rect>/<Line> also passed
               percentage-string coords, which silently forced data mode.) -->
          <g class="target-ranges">
            <rect
              x={0}
              y={context.yScale(bgValue(180))}
              width={context.width}
              height={context.yScale(bgValue(70)) - context.yScale(bgValue(180))}
              fill="hsl(var(--success))"
              fill-opacity="0.1"
            />
            <line
              x1={0}
              x2={context.width}
              y1={context.yScale(bgValue(180))}
              y2={context.yScale(bgValue(180))}
              stroke="hsl(var(--destructive))"
              stroke-width="1"
              stroke-dasharray="5,5"
              opacity="0.7"
            />
            <line
              x1={0}
              x2={context.width}
              y1={context.yScale(bgValue(70))}
              y2={context.yScale(bgValue(70))}
              stroke="hsl(var(--destructive))"
              stroke-width="1"
              stroke-dasharray="5,5"
              opacity="0.7"
            />
          </g>

          <!-- Box plots -->
          <g class="box-plots">
            {#each chartData as data (data.hour)}
              {@const cx = context.xScale(data.hour)}
              {@const yQ1 = context.yScale(data.q1)}
              {@const yQ3 = context.yScale(data.q3)}
              {@const yMedian = context.yScale(data.median)}
              {@const yMin = context.yScale(data.min)}
              {@const yMax = context.yScale(data.max)}

              <!-- Box (IQR) -->
              <rect
                x={cx - boxHalfWidth}
                y={yQ3}
                width={boxHalfWidth * 2}
                height={yQ1 - yQ3}
                fill="hsl(var(--primary))"
                fill-opacity="0.3"
                stroke="hsl(var(--primary))"
                stroke-width="2"
              />
              <!-- Median line -->
              <line
                x1={cx - boxHalfWidth}
                x2={cx + boxHalfWidth}
                y1={yMedian}
                y2={yMedian}
                stroke="hsl(var(--primary))"
                stroke-width="3"
              />
              <!-- Upper whisker -->
              <line
                x1={cx}
                x2={cx}
                y1={yQ3}
                y2={yMax}
                stroke="hsl(var(--primary))"
                stroke-width="1"
              />
              <!-- Lower whisker -->
              <line
                x1={cx}
                x2={cx}
                y1={yQ1}
                y2={yMin}
                stroke="hsl(var(--primary))"
                stroke-width="1"
              />
              <!-- Whisker caps -->
              <line
                x1={cx - capHalfWidth}
                x2={cx + capHalfWidth}
                y1={yMax}
                y2={yMax}
                stroke="hsl(var(--primary))"
                stroke-width="1"
              />
              <line
                x1={cx - capHalfWidth}
                x2={cx + capHalfWidth}
                y1={yMin}
                y2={yMin}
                stroke="hsl(var(--primary))"
                stroke-width="1"
              />
              <!-- Outliers -->
              {#each data.outliers as outlier}
                <circle
                  cx={cx}
                  cy={context.yScale(outlier)}
                  r="2"
                  fill="hsl(var(--destructive))"
                  stroke="hsl(var(--destructive))"
                  stroke-width="1"
                />
              {/each}
            {/each}
          </g>

          <!-- Tooltip -->
          <Tooltip.Root
            class="bg-popover text-popover-foreground p-3 rounded-md shadow-lg border"
          >
            {#snippet children({ data })}
              <div class="space-y-1">
                <div class="font-semibold">{formatHour(data.hour)}</div>
                <div class="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
                  <div>Max: {data.max}</div>
                  <div>Q3: {data.q3}</div>
                  <div>Median: {data.median}</div>
                  <div>Q1: {data.q1}</div>
                  <div>Min: {data.min}</div>
                  {#if data.outliers.length > 0}
                    <div class="col-span-2">Outliers: {data.outliers.length}</div>
                  {/if}
                </div>
              </div>
            {/snippet}
          </Tooltip.Root>
        </Svg>
      {/snippet}
    </Chart>
  {:else}
    <div class="flex items-center justify-center h-full text-muted-foreground">
      <div class="text-center">
        <p class="text-lg font-medium">No data available</p>
        <p class="text-sm">
          No glucose readings found for box plot visualization
        </p>
      </div>
    </div>
  {/if}
</div>

<style>
  /* Custom styles for better visualization */
  :global(.target-ranges) {
    pointer-events: none;
  }

  :global(.box-plots rect:hover) {
    fill-opacity: 0.5;
  }

  :global(.box-plots line:hover) {
    stroke-width: 2;
  }
</style>
