<script lang="ts">
  import { AreaChart, Rule, Legend } from "layerchart";
  import { scaleOrdinal } from "d3-scale";
  import SiteChangeIcon from "$lib/components/icons/SiteChangeIcon.svelte";
  import { AlertCircle } from "lucide-svelte";

  // Local type definitions for site change impact analysis
  interface SiteChangeImpactDataPoint {
    minutesFromChange?: number;
    averageGlucose?: number;
    medianGlucose?: number;
    stdDev?: number;
    count?: number;
    percentile10?: number;
    percentile25?: number;
    percentile75?: number;
    percentile90?: number;
  }

  interface SiteChangeImpactSummary {
    avgGlucoseBeforeChange?: number;
    avgGlucoseAfterChange?: number;
    timeInRangeBeforeChange?: number;
    timeInRangeAfterChange?: number;
    percentImprovement?: number;
  }

  interface SiteChangeImpactAnalysis {
    dataPoints?: SiteChangeImpactDataPoint[];
    summary?: SiteChangeImpactSummary;
    hasSufficientData?: boolean;
    siteChangeCount?: number;
    hoursBeforeChange?: number;
    hoursAfterChange?: number;
  }

  // Local interface with required fields for chart rendering
  interface SiteChangeImpactDataPointValid {
    minutesFromChange: number;
    averageGlucose: number;
    medianGlucose: number;
    stdDev: number;
    count: number;
    percentile10: number;
    percentile25: number;
    percentile75: number;
    percentile90: number;
  }

  interface Props {
    analysis: SiteChangeImpactAnalysis | null;
  }

  let { analysis }: Props = $props();

  // Format hours for X-axis (ticks are always on hour boundaries)
  function formatHoursFromChange(minutes: number): string {
    const hours = Math.round(minutes / 60);
    if (hours === 0) return "Site Change";
    if (hours > 0) return `+${hours}h`;
    return `${hours}h`;
  }

  // Generate explicit hourly tick values from the x-axis domain
  const hourlyTicks = $derived.by(() => {
    const [minMinutes, maxMinutes] = xDomain;
    const ticks: number[] = [];
    const startHour = Math.ceil(minMinutes / 60);
    const endHour = Math.floor(maxMinutes / 60);
    for (let h = startHour; h <= endHour; h++) {
      ticks.push(h * 60);
    }
    return ticks;
  });

  // Filter and validate data points to ensure all required fields are present
  const chartData = $derived.by((): SiteChangeImpactDataPointValid[] => {
    if (!analysis?.dataPoints) return [];

    return analysis.dataPoints
      .filter(
        (
          d: SiteChangeImpactDataPoint
        ): d is typeof d & {
          minutesFromChange: number;
          medianGlucose: number;
          percentile10: number;
          percentile25: number;
          percentile75: number;
          percentile90: number;
        } =>
          d.minutesFromChange !== undefined &&
          d.medianGlucose !== undefined &&
          d.percentile10 !== undefined &&
          d.percentile25 !== undefined &&
          d.percentile75 !== undefined &&
          d.percentile90 !== undefined
      )
      .map((d: SiteChangeImpactDataPoint) => ({
        minutesFromChange: d.minutesFromChange!,
        averageGlucose: d.averageGlucose ?? d.medianGlucose!,
        medianGlucose: d.medianGlucose!,
        stdDev: d.stdDev ?? 0,
        count: d.count ?? 0,
        percentile10: d.percentile10!,
        percentile25: d.percentile25!,
        percentile75: d.percentile75!,
        percentile90: d.percentile90!,
      }));
  });

  const yDomain = $derived.by(() => {
    if (chartData.length === 0) return [40, 300];
    const allValues = chartData.flatMap((d) => [
      d.percentile10,
      d.percentile90,
    ]);
    const min = Math.min(...allValues);
    const max = Math.max(...allValues);
    return [Math.max(40, min - 20), Math.min(400, max + 20)];
  });

  const xDomain = $derived.by(() => {
    if (!analysis?.hoursBeforeChange || !analysis?.hoursAfterChange)
      return [-720, 1440];
    return [-analysis.hoursBeforeChange * 60, analysis.hoursAfterChange * 60];
  });

  const legendScale = scaleOrdinal<string, string>()
    .domain([
      "10th-25th / 75th-90th percentile",
      "25th-75th percentile",
      "Median glucose",
      "Target range (70-180)",
    ])
    .range([
      "var(--percentile-outer)",
      "var(--percentile-inner)",
      "var(--percentile-median)",
      "transparent",
    ]);
</script>

<div class="w-full">
  {#if analysis && analysis.hasSufficientData && chartData.length > 0}
    <div class="h-[400px] w-full">
      <AreaChart
        data={chartData}
        x={(d) => d.minutesFromChange}
        y={(d) => d.medianGlucose}
        renderContext="svg"
        {xDomain}
        {yDomain}
        series={[
          {
            key: "p10_p25",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.percentile25,
              (d: SiteChangeImpactDataPointValid) => d.percentile10,
            ],
            color: "var(--percentile-outer)",
            label: "10th-25th",
          },
          {
            key: "p25_median",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.medianGlucose,
              (d: SiteChangeImpactDataPointValid) => d.percentile25,
            ],
            color: "var(--percentile-inner)",
            label: "25th-Median",
          },
          {
            key: "median",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.medianGlucose,
              (d: SiteChangeImpactDataPointValid) => d.medianGlucose,
            ],
            color: "var(--percentile-median)",
            props: {
              line: { strokeWidth: 2 },
            },
            label: "Median",
          },
          {
            key: "median_p75",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.medianGlucose,
              (d: SiteChangeImpactDataPointValid) => d.percentile75,
            ],
            color: "var(--percentile-inner)",
            label: "Median-75th",
          },
          {
            key: "p75_p90",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.percentile75,
              (d: SiteChangeImpactDataPointValid) => d.percentile90,
            ],
            color: "var(--percentile-outer)",
            label: "75th-90th",
          },
        ]}
        seriesLayout="overlap"
        tooltip={{ mode: "bisect-x" }}
        props={{
          area: { motion: { type: "tween", duration: 200 } },
          xAxis: {
            motion: { type: "tween", duration: 200 },
            tickMultiline: true,
            format: formatHoursFromChange,
            ticks: hourlyTicks,
          },
          yAxis: {
            label: "Glucose (mg/dL)",
          },
        }}
        padding={{ top: 20, right: 20, bottom: 40, left: 50 }}
      >
        <!-- Target range overlay (70-180 mg/dL) -->
        {#snippet aboveMarks()}
          <!-- Horizontal reference lines for target range -->
          <Rule y={70} class="stroke-success/50 stroke-1 stroke-dashed" />
          <Rule y={180} class="stroke-warning/50 stroke-1 stroke-dashed" />
          <!-- Vertical line at site change point -->
          <Rule x={0} class="stroke-primary stroke-2" />
        {/snippet}
      </AreaChart>
    </div>

    <!-- Legend -->
    <div class="mt-4 flex justify-center">
      <Legend
        scale={legendScale}
        variant="swatches"
        classes={{
          label: "text-xs text-muted-foreground",
          swatch: "rounded-sm",
        }}
      />
    </div>

    <!-- Summary Statistics -->
    {#if analysis.summary}
      <div class="mt-6 grid grid-cols-2 gap-4 md:grid-cols-4">
        <div class="rounded-lg bg-muted/50 p-3 text-center">
          <div class="text-sm text-muted-foreground">Avg Before</div>
          <div class="text-xl font-semibold">
            {analysis.summary.avgGlucoseBeforeChange?.toFixed(0)}
            <span class="text-xs text-muted-foreground">mg/dL</span>
          </div>
        </div>
        <div class="rounded-lg bg-muted/50 p-3 text-center">
          <div class="text-sm text-muted-foreground">Avg After</div>
          <div class="text-xl font-semibold">
            {analysis.summary.avgGlucoseAfterChange?.toFixed(0)}
            <span class="text-xs text-muted-foreground">mg/dL</span>
          </div>
        </div>
        <div class="rounded-lg bg-muted/50 p-3 text-center">
          <div class="text-sm text-muted-foreground">TIR Before</div>
          <div class="text-xl font-semibold">
            {analysis.summary.timeInRangeBeforeChange?.toFixed(0)}%
          </div>
        </div>
        <div class="rounded-lg bg-muted/50 p-3 text-center">
          <div class="text-sm text-muted-foreground">TIR After</div>
          <div class="text-xl font-semibold">
            {analysis.summary.timeInRangeAfterChange?.toFixed(0)}%
          </div>
        </div>
      </div>

      {#if analysis.summary?.percentImprovement !== undefined && analysis.summary?.percentImprovement > 0}
        <div class="mt-4 rounded-lg bg-success/10 p-3 text-center text-success">
          <span class="font-medium">
            ↓ {analysis.summary?.percentImprovement.toFixed(1)}% improvement
          </span>
          <span class="text-sm opacity-80">after site change</span>
        </div>
      {:else if analysis.summary?.percentImprovement !== undefined && analysis.summary?.percentImprovement < 0}
        <div class="mt-4 rounded-lg bg-warning/10 p-3 text-center text-warning">
          <span class="font-medium">
            ↑ {Math.abs(analysis.summary?.percentImprovement).toFixed(1)}%
            higher
          </span>
          <span class="text-sm opacity-80">after site change</span>
        </div>
      {/if}
    {/if}
  {:else if analysis && !analysis.hasSufficientData}
    <div
      class="flex h-[400px] w-full flex-col items-center justify-center text-muted-foreground"
    >
      <AlertCircle class="mx-auto h-10 w-10 opacity-30" />
      <p class="mt-2 font-medium">Insufficient Data</p>
      <p class="text-sm text-center max-w-md">
        {#if (analysis.siteChangeCount ?? 0) < 2}
          At least 2 site changes are required for meaningful analysis.
          Currently found: {analysis.siteChangeCount ?? 0} site change(s).
        {:else}
          Not enough glucose readings around your site changes to generate a
          reliable analysis.
        {/if}
      </p>
    </div>
  {:else}
    <div
      class="flex h-[400px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <SiteChangeIcon class="mx-auto h-10 w-10 opacity-30" />
        <p class="mt-2 font-medium">No site change data available</p>
        <p class="text-sm">
          Site changes are required to analyze glucose patterns
        </p>
      </div>
    </div>
  {/if}
</div>
