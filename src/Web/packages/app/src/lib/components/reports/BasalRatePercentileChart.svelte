<script lang="ts">
  import { AreaChart } from "layerchart";
  import type { HourlyBasalPercentileData } from "$lib/api";
  import { Layers, Loader2 } from "lucide-svelte";

  interface Props {
    data: HourlyBasalPercentileData[];
    loading?: boolean;
  }

  let { data, loading = false }: Props = $props();

  // Map backend data to chart format
  const chartData = $derived(data ?? []);

  const maxRate = $derived.by(() => {
    if (chartData.length === 0) return 3;
    const allRates = chartData.flatMap((d) => [d.p90, d.median]);
    return Math.max(3, Math.ceil(Math.max(...allRates) * 1.2));
  });

  // Format hour for display
  function formatHour(hour: number): string {
    if (hour === 0) return "12 AM";
    if (hour < 12) return `${hour} AM`;
    if (hour === 12) return "12 PM";
    return `${hour - 12} PM`;
  }
</script>

<div class="w-full">
  {#if loading}
    <div
      class="flex h-[400px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <Loader2 class="mx-auto h-10 w-10 animate-spin opacity-50" />
        <p class="mt-2 font-medium">Loading basal data...</p>
      </div>
    </div>
  {:else if chartData.length > 0 && chartData.some((d) => d.count > 0)}
    <div class="h-[400px] w-full">
      <AreaChart
        data={chartData}
        x={(d) => d.hour}
        y={(d) => d.median}
        renderContext="svg"
        legend
        series={[
          {
            key: "p10",
            value: [
              (d: HourlyBasalPercentileData) => d.p25,
              (d: HourlyBasalPercentileData) => d.p10,
            ],
            color: "var(--chart-1)",
            label: "P10-P25",
          },
          {
            key: "p25",
            value: [
              (d: HourlyBasalPercentileData) => d.median,
              (d: HourlyBasalPercentileData) => d.p25,
            ],
            color: "var(--chart-2)",
            label: "P25-Median",
          },
          {
            key: "median",
            value: [
              (d: HourlyBasalPercentileData) => d.median,
              (d: HourlyBasalPercentileData) => d.median,
            ],
            color: "hsl(var(--primary))",
            props: {
              line: { strokeWidth: 2 },
            },
            label: "Median",
          },
          {
            key: "p75",
            value: [
              (d: HourlyBasalPercentileData) => d.median,
              (d: HourlyBasalPercentileData) => d.p75,
            ],
            color: "var(--chart-3)",
            label: "Median-P75",
          },
          {
            key: "p90",
            value: [
              (d: HourlyBasalPercentileData) => d.p75,
              (d: HourlyBasalPercentileData) => d.p90,
            ],
            color: "var(--chart-1)",
            label: "P75-P90",
          },
        ]}
        xDomain={[0, 23]}
        yDomain={[0, maxRate]}
        seriesLayout="overlap"
        tooltip={{ mode: "bisect-x" }}
        props={{
          area: { motion: { type: "tween", duration: 200 } },
          xAxis: {
            motion: { type: "tween", duration: 200 },
            tickMultiline: true,
            format: formatHour,
          },
          yAxis: {
            label: "Basal Rate (U/hr)",
          },
        }}
        padding={{ top: 20, right: 20, bottom: 40, left: 50 }}
      />
    </div>

    <!-- Legend explanation -->
    <div
      class="mt-4 flex flex-wrap items-center justify-center gap-4 text-xs text-muted-foreground"
    >
      <div class="flex items-center gap-1.5">
        <div
          class="h-3 w-3 rounded-sm"
          style="background-color: var(--chart-1); opacity: 0.5"
        ></div>
        <span>10th-25th / 75th-90th percentile</span>
      </div>
      <div class="flex items-center gap-1.5">
        <div
          class="h-3 w-3 rounded-sm"
          style="background-color: var(--chart-2)"
        ></div>
        <span>25th-75th percentile</span>
      </div>
      <div class="flex items-center gap-1.5">
        <div
          class="h-0.5 w-4 rounded"
          style="background-color: hsl(var(--primary))"
        ></div>
        <span>Median basal rate</span>
      </div>
    </div>
  {:else}
    <div
      class="flex h-[400px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <Layers class="mx-auto h-10 w-10 opacity-30" />
        <p class="mt-2 font-medium">No basal data available</p>
        <p class="text-sm">
          No temp basal or basal treatments found in this period
        </p>
      </div>
    </div>
  {/if}
</div>
