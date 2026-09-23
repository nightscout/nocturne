<script lang="ts">
  import { AreaChart } from "layerchart";
  import { timeFormat } from "$lib/stores/appearance-store.svelte";
  import { bg } from "$lib/utils/formatting";
  import { BarChart2 } from "lucide-svelte";
  import type { AveragedStats } from "$lib/api";
  import {
    CHART_TEXTURES,
    patternClass,
    type GlucoseRange,
  } from "$lib/components/charts/print/chart-print-patterns";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";

  interface Props {
    averagedStats?: AveragedStats[];
  }

  interface HourlyRangeData {
    hour: number;
    veryLow: number;
    low: number;
    normal: number;
    aboveTarget: number;
    high: number;
    veryHigh: number;
    count: number;
  }

  let { averagedStats }: Props = $props();

  function transformToChartData(stats: AveragedStats[]): HourlyRangeData[] {
    return stats.map((s) => ({
      hour: s.hour ?? 0,
      veryLow: s.timeInRange?.veryLow ?? 0,
      low: s.timeInRange?.low ?? 0,
      normal: s.timeInRange?.normal ?? 0,
      aboveTarget: s.timeInRange?.aboveTarget ?? 0,
      high: s.timeInRange?.high ?? 0,
      veryHigh: s.timeInRange?.veryHigh ?? 0,
      count: s.count ?? 0,
    }));
  }

  // Format hour for display based on user's time format preference
  function formatHour(hour: number): string {
    if (timeFormat.current === "24") {
      return hour.toString().padStart(2, "0");
    }
    if (hour === 0) return "12AM";
    if (hour < 12) return `${hour}AM`;
    if (hour === 12) return "12PM";
    return `${hour - 12}PM`;
  }

  const band = (key: string, label: string, texture: GlucoseRange) => ({
    key,
    label,
    texture,
    color: CHART_TEXTURES[texture].color,
    props: { class: patternClass(texture) },
  });

  const chartSeries = $derived([
    band("veryLow", `<${bg(54)}`, "very-low"),
    band("low", `${bg(54)}-${bg(63)}`, "low"),
    band("normal", `${bg(63)}-${bg(140)}`, "tight-range"),
    band("aboveTarget", `${bg(140)}-${bg(180)}`, "in-range"),
    band("high", `${bg(180)}-${bg(200)}`, "high"),
    band("veryHigh", `>${bg(200)}`, "very-high"),
  ]);

  // Derived chart data
  const chartData = $derived(
    averagedStats && averagedStats.length > 0
      ? transformToChartData(averagedStats)
      : []
  );
  const hasData = $derived(chartData?.some((d) => d.count > 0));
</script>

<div class="w-full">
  {#if hasData}
    <div class="h-[350px] w-full">
      <AreaChart
        data={chartData}
        x="hour"
        yDomain={[0, 100]}
        series={chartSeries}
        seriesLayout="stack"
        props={{
          xAxis: {
            format: formatHour,
          },
          yAxis: {
            label: "Percentage",
            format: (v: number) => `${v}%`,
          },
        }}
        padding={{ top: 20, right: 20, bottom: 40, left: 50 }}
      />
    </div>
    <ChartKey
      class="pt-2"
      items={chartSeries.map((s) => ({ texture: s.texture, label: s.label }))}
    />
  {:else}
    <div
      class="flex h-[350px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <BarChart2 class="mx-auto h-10 w-10 opacity-30" />
        <p class="mt-2 font-medium">No glucose data available</p>
        <p class="text-sm">Hourly distribution requires glucose entries</p>
      </div>
    </div>
  {/if}
</div>
