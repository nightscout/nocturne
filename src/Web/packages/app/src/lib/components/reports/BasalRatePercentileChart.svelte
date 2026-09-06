<script lang="ts">
  import { AreaChart, Legend } from "layerchart";
  import { scaleOrdinal } from "d3-scale";
  import { Layers, Loader2 } from "lucide-svelte";

  // Local type definition for hourly basal percentile data
  interface HourlyBasalPercentileData {
    hour?: number;
    p10?: number;
    p25?: number;
    median?: number;
    p75?: number;
    p90?: number;
    count?: number;
  }

  interface Props {
    data: HourlyBasalPercentileData[];
    loading?: boolean;
  }

  let { data, loading = false }: Props = $props();

  // Map backend data to chart format
  const chartData = $derived(data ?? []);

  const maxRate = $derived.by(() => {
    if (chartData.length === 0) return 3;
    const allRates = chartData.flatMap((d) => [d.p90, d.median]).filter((v): v is number => v !== undefined);
    return Math.max(3, Math.ceil(Math.max(...allRates) * 1.2));
  });

  // Format hour for display
  function formatHour(hour: number): string {
    if (hour === 0) return "12 AM";
    if (hour < 12) return `${hour} AM`;
    if (hour === 12) return "12 PM";
    return `${hour - 12} PM`;
  }

  const legendScale = scaleOrdinal<string, string>()
    .domain([
      "10th-25th / 75th-90th percentile",
      "25th-75th percentile",
      "Median basal rate",
    ])
    .range([
      "var(--chart-1)",
      "var(--chart-2)",
      "var(--primary)",
    ]);
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
  {:else if chartData.length > 0 && chartData.some((d) => (d.count ?? 0) > 0)}
    <div class="h-[400px] w-full">
      <AreaChart
        data={chartData}
        x={(d) => d.hour}
        y={(d) => d.median}
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
            color: "var(--primary)",
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
        tooltipContext={{ mode: "bisect-x" }}
        props={{
          xAxis: {
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
