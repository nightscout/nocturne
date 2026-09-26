<script lang="ts">
  import { AreaChart, Rule } from "layerchart";
  import { patternClass } from "$lib/components/charts/print/chart-print-patterns";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import SiteChangeIcon from "$lib/components/icons/SiteChangeIcon.svelte";
  import { AlertCircle } from "lucide-svelte";
  import { bg, bgValue, bgLabel, bgRange } from "$lib/utils/formatting";
  import type {
    SiteChangeImpactAnalysis,
    SiteChangeImpactDataPoint,
  } from "$lib/api";

  type CheckedImpactFields = Required<
    Pick<
      SiteChangeImpactDataPoint,
      | "minutesFromChange"
      | "medianGlucose"
      | "percentile10"
      | "percentile25"
      | "percentile75"
      | "percentile90"
    >
  >;

  // Chart points with every plotted field present; the API leaves them optional.
  type SiteChangeImpactDataPointValid = Required<SiteChangeImpactDataPoint>;

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

  const MAX_X_TICKS = 13;
  const TICK_STEPS_HOURS = [1, 2, 3, 4, 6, 12];

  // Hour ticks anchored on the change itself, thinned so labels never touch.
  const hourlyTicks = $derived.by(() => {
    const [minMinutes, maxMinutes] = xDomain;
    const startHour = Math.ceil(minMinutes / 60);
    const endHour = Math.floor(maxMinutes / 60);
    const span = endHour - startHour;
    const step = TICK_STEPS_HOURS.find((s) => span / s < MAX_X_TICKS) ?? 12;
    const ticks: number[] = [];
    for (let h = Math.ceil(startHour / step) * step; h <= endHour; h += step) {
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
        ): d is SiteChangeImpactDataPoint & CheckedImpactFields =>
          d.minutesFromChange !== undefined &&
          d.medianGlucose !== undefined &&
          d.percentile10 !== undefined &&
          d.percentile25 !== undefined &&
          d.percentile75 !== undefined &&
          d.percentile90 !== undefined
      )
      // Convert glucose fields to the user's display units; minutes/count are unitless.
      .map((d: SiteChangeImpactDataPoint) => ({
        minutesFromChange: d.minutesFromChange!,
        averageGlucose: bgValue(d.averageGlucose ?? d.medianGlucose!),
        medianGlucose: bgValue(d.medianGlucose!),
        stdDev: bgValue(d.stdDev ?? 0),
        count: d.count ?? 0,
        percentile10: bgValue(d.percentile10!),
        percentile25: bgValue(d.percentile25!),
        percentile75: bgValue(d.percentile75!),
        percentile90: bgValue(d.percentile90!),
      }));
  });

  // chartData is already in display units; keep the domain/margins in the same units.
  const yDomain = $derived.by(() => {
    if (chartData.length === 0) return [bgValue(40), bgValue(300)];
    const allValues = chartData.flatMap((d) => [
      d.percentile10,
      d.percentile90,
    ]);
    const min = Math.min(...allValues);
    const max = Math.max(...allValues);
    return [
      Math.max(bgValue(40), min - bgValue(20)),
      Math.min(bgValue(400), max + bgValue(20)),
    ];
  });

  const xDomain = $derived.by(() => {
    if (!analysis?.hoursBeforeChange || !analysis?.hoursAfterChange)
      return [-720, 1440];
    return [-analysis.hoursBeforeChange * 60, analysis.hoursAfterChange * 60];
  });

  const outer = { color: "var(--percentile-outer)", props: { class: patternClass("percentile-outer") } };
  const inner = { color: "var(--percentile-inner)", props: { class: patternClass("percentile-inner") } };
</script>

<div class="@container w-full">
  {#if analysis && analysis.hasSufficientData && chartData.length > 0}
    <div class="h-[400px] w-full">
      <AreaChart
        data={chartData}
        x={(d) => d.minutesFromChange}
        y={(d) => d.medianGlucose}
        {xDomain}
        {yDomain}
        series={[
          {
            key: "p10_p25",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.percentile25,
              (d: SiteChangeImpactDataPointValid) => d.percentile10,
            ],
            ...outer,
            label: "10th-25th",
          },
          {
            key: "p25_median",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.medianGlucose,
              (d: SiteChangeImpactDataPointValid) => d.percentile25,
            ],
            ...inner,
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
            ...inner,
            label: "Median-75th",
          },
          {
            key: "p75_p90",
            value: [
              (d: SiteChangeImpactDataPointValid) => d.percentile75,
              (d: SiteChangeImpactDataPointValid) => d.percentile90,
            ],
            ...outer,
            label: "75th-90th",
          },
        ]}
        seriesLayout="overlap"
        tooltipContext={{ mode: "bisect-x" }}
        props={{
          area: { motion: { type: "tween", duration: 200 } },
          xAxis: {
            motion: { type: "tween", duration: 200 },
            tickMultiline: true,
            format: formatHoursFromChange,
            ticks: hourlyTicks,
          },
          yAxis: {
            label: `Glucose (${bgLabel()})`,
          },
        }}
        padding={{ top: 20, right: 20, bottom: 40, left: 50 }}
      >
        <!-- Target range overlay (70-180 mg/dL, plotted in display units) -->
        {#snippet aboveMarks()}
          <!-- Horizontal reference lines for target range -->
          <Rule y={bgValue(70)} class="stroke-success/50 stroke-1" dashArray="4 4" />
          <Rule y={bgValue(180)} class="stroke-warning/50 stroke-1" dashArray="4 4" />
          <!-- Vertical line at site change point -->
          <Rule x={0} class="stroke-primary stroke-2" />
        {/snippet}
      </AreaChart>
    </div>

    <ChartKey
      class="mt-4"
      items={[
        { texture: "percentile-outer", label: "10th–25th / 75th–90th percentile" },
        { texture: "percentile-inner", label: "25th–75th percentile" },
        { texture: "percentile-median", label: "Median glucose", shape: "line" },
        { texture: "target-range-limit", label: `Target range (${bgRange(70, 180)})`, shape: "line" },
      ]}
    />

    {#if analysis.summary}
      <dl class="mt-6 grid grid-cols-2 gap-4 border-t border-border pt-4 @lg:grid-cols-4 print:grid-cols-4">
        <div>
          <dt class="text-sm text-muted-foreground">Avg before</dt>
          <dd class="m-0 text-lg font-semibold tabular-nums">
            {analysis.summary.avgGlucoseBeforeChange != null
              ? bg(analysis.summary.avgGlucoseBeforeChange)
              : "–"}
            <span class="text-xs font-normal text-muted-foreground">{bgLabel()}</span>
          </dd>
        </div>
        <div>
          <dt class="text-sm text-muted-foreground">Avg after</dt>
          <dd class="m-0 text-lg font-semibold tabular-nums">
            {analysis.summary.avgGlucoseAfterChange != null
              ? bg(analysis.summary.avgGlucoseAfterChange)
              : "–"}
            <span class="text-xs font-normal text-muted-foreground">{bgLabel()}</span>
          </dd>
        </div>
        <div>
          <dt class="text-sm text-muted-foreground">TIR before</dt>
          <dd class="m-0 text-lg font-semibold tabular-nums">
            {analysis.summary.timeInRangeBeforeChange?.toFixed(0)}<span class="text-xs font-normal text-muted-foreground">%</span>
          </dd>
        </div>
        <div>
          <dt class="text-sm text-muted-foreground">TIR after</dt>
          <dd class="m-0 text-lg font-semibold tabular-nums">
            {analysis.summary.timeInRangeAfterChange?.toFixed(0)}<span class="text-xs font-normal text-muted-foreground">%</span>
          </dd>
        </div>
      </dl>
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
