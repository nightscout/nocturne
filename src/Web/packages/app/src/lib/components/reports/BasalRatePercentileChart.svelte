<script lang="ts">
  import { AreaChart } from "layerchart";
  import type { Treatment } from "$lib/api";
  import { Layers } from "lucide-svelte";

  interface HourlyBasalData {
    hour: number;
    p10: number;
    p25: number;
    median: number;
    p75: number;
    p90: number;
    count: number;
  }

  interface Props {
    treatments: Treatment[];
  }

  let { treatments }: Props = $props();

  // Calculate percentiles from an array of numbers
  function percentile(arr: number[], p: number): number {
    if (arr.length === 0) return 0;
    const sorted = [...arr].sort((a, b) => a - b);
    const index = (p / 100) * (sorted.length - 1);
    const lower = Math.floor(index);
    const upper = Math.ceil(index);
    if (lower === upper) return sorted[lower];
    return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
  }

  // Process treatments to get hourly basal rate percentiles
  function processBasalData(treatments: Treatment[]): HourlyBasalData[] {
    // Group basal treatments by hour
    const hourlyRates: Map<number, number[]> = new Map();

    // Initialize all hours
    for (let h = 0; h < 24; h++) {
      hourlyRates.set(h, []);
    }

    // Filter to only basal-related treatments
    const basalTreatments = treatments.filter((t) => {
      const eventType = t.eventType?.toLowerCase() || "";
      return (
        eventType.includes("basal") ||
        eventType.includes("temp basal") ||
        eventType === "tempbasal" ||
        t.rate !== undefined ||
        t.absolute !== undefined
      );
    });

    // Process each basal treatment
    for (const treatment of basalTreatments) {
      const date = new Date(
        treatment.created_at ??
          treatment.eventTime ??
          treatment.mills ??
          Date.now()
      );
      if (isNaN(date.getTime())) continue;

      const hour = date.getHours();
      const rate = treatment.rate ?? treatment.absolute ?? 0;

      if (rate > 0) {
        hourlyRates.get(hour)?.push(rate);
      }
    }

    // Calculate percentiles for each hour
    const data: HourlyBasalData[] = [];

    for (let hour = 0; hour < 24; hour++) {
      const rates = hourlyRates.get(hour) || [];

      if (rates.length > 0) {
        data.push({
          hour,
          p10: percentile(rates, 10),
          p25: percentile(rates, 25),
          median: percentile(rates, 50),
          p75: percentile(rates, 75),
          p90: percentile(rates, 90),
          count: rates.length,
        });
      } else {
        // No data for this hour, use zeros or interpolate
        data.push({
          hour,
          p10: 0,
          p25: 0,
          median: 0,
          p75: 0,
          p90: 0,
          count: 0,
        });
      }
    }

    return data;
  }

  // Use derived values instead of effect to avoid infinite loops
  const chartData = $derived(
    treatments && treatments.length > 0 ? processBasalData(treatments) : []
  );

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
  {#if chartData.length > 0 && chartData.some((d) => d.count > 0)}
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
              (d: HourlyBasalData) => d.p25,
              (d: HourlyBasalData) => d.p10,
            ],
            color: "var(--chart-1)",
            label: "P10-P25",
          },
          {
            key: "p25",
            value: [
              (d: HourlyBasalData) => d.median,
              (d: HourlyBasalData) => d.p25,
            ],
            color: "var(--chart-2)",
            label: "P25-Median",
          },
          {
            key: "median",
            value: [
              (d: HourlyBasalData) => d.median,
              (d: HourlyBasalData) => d.median,
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
              (d: HourlyBasalData) => d.median,
              (d: HourlyBasalData) => d.p75,
            ],
            color: "var(--chart-3)",
            label: "Median-P75",
          },
          {
            key: "p90",
            value: [
              (d: HourlyBasalData) => d.p75,
              (d: HourlyBasalData) => d.p90,
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
