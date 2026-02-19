<script lang="ts">
  import { AreaChart } from "layerchart";
  import type { AveragedStats } from "$lib/api";
  import { timeFormat } from "$lib/stores/appearance-store.svelte";
  import { bg } from "$lib/utils/formatting";
  import { BarChart2 } from "lucide-svelte";

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
      veryHigh: (s.timeInRange?.veryHigh ?? 0) + (s.timeInRange?.veryHigh ?? 0),
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

  // Chart series configuration - labels respect mmol/mg/dL preference
  // Using $derived to make labels reactive to unit changes
  const chartSeries = $derived([
    { key: "veryLow", label: `<${bg(54)}`, color: "var(--glucose-very-low)" },
    { key: "low", label: `${bg(54)}-${bg(63)}`, color: "var(--glucose-low)" },
    { key: "normal", label: `${bg(63)}-${bg(140)}`, color: "var(--glucose-tight-range)" },
    { key: "aboveTarget", label: `${bg(140)}-${bg(180)}`, color: "var(--glucose-in-range)" },
    { key: "high", label: `${bg(180)}-${bg(200)}`, color: "var(--glucose-high)" },
    { key: "veryHigh", label: `>${bg(200)}`, color: "var(--glucose-very-high)" },
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
        legend
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
