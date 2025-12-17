<script lang="ts">
  import type { Entry } from "$lib/api";
  import type { AveragedStats } from "$lib/api";
  import { calculateAveragedStats } from "$lib/data/statistics.remote";
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import { AreaChart } from "layerchart";
  import { onMount } from "svelte";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { BarChart3 } from "lucide-svelte";
  import {
    glucoseUnits,
    timeFormat,
  } from "$lib/stores/appearance-store.svelte";
  import { convertToDisplayUnits } from "$lib/utils/formatting";

  let {
    entries,
    averagedStats,
  }: {
    entries?: Entry[];
    averagedStats?: AveragedStats[];
  } = $props();

  // Local state for calculated stats if not provided via props
  let calculatedStats = $state<AveragedStats[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  // Use props if available, otherwise fall back to locally calculated stats
  const rawData = $derived(
    averagedStats?.length ? averagedStats : calculatedStats
  );

  // Reactive unit-aware data transformation
  const units = $derived(glucoseUnits.current);
  const isMMOL = $derived(units === "mmol");

  // Convert data to display units
  const data = $derived(
    rawData.map((d) => ({
      ...d,
      median: convertToDisplayUnits(d.median, units),
      percentiles: d.percentiles
        ? {
            p10: convertToDisplayUnits(d.percentiles.p10 ?? 0, units),
            p25: convertToDisplayUnits(d.percentiles.p25 ?? 0, units),
            p75: convertToDisplayUnits(d.percentiles.p75 ?? 0, units),
            p90: convertToDisplayUnits(d.percentiles.p90 ?? 0, units),
          }
        : undefined,
    }))
  );

  // Dynamic Y-axis domain based on units
  const yDomain = $derived<[number, number]>(isMMOL ? [0, 22.2] : [0, 400]);

  // Convert threshold values to display units
  const lowThreshold = $derived(
    convertToDisplayUnits(DEFAULT_THRESHOLDS.low ?? 70, units)
  );
  const highThreshold = $derived(
    convertToDisplayUnits(DEFAULT_THRESHOLDS.high ?? 180, units)
  );

  // Time format for X-axis labels
  const is24Hour = $derived(timeFormat.current === "24");

  // Format hour for X-axis tick label
  function formatHour(hour: number): string {
    if (is24Hour) {
      return `${hour.toString().padStart(2, "0")}:00`;
    }
    if (hour === 0) return "12am";
    if (hour === 12) return "12pm";
    if (hour < 12) return `${hour}am`;
    return `${hour - 12}pm`;
  }

  onMount(async () => {
    // If we already have averaged stats, we don't need to load
    if (averagedStats?.length) {
      loading = false;
      return;
    }

    // If we have entries but no stats, calculate them
    if (entries?.length) {
      try {
        const stats = await calculateAveragedStats({ entries });
        calculatedStats = stats;
        loading = false;
      } catch (err) {
        console.error("Failed to calculate averaged stats:", err);
        error = err instanceof Error ? err.message : "Failed to load data";
        loading = false;
      }
    } else {
      // No data to work with
      loading = false;
    }
  });
</script>

{#if loading}
  <div class="flex h-full w-full flex-col items-center justify-center gap-3">
    <Skeleton class="h-3/4 w-full rounded-lg" />
    <div class="flex items-center gap-2 text-sm text-muted-foreground">
      <BarChart3 class="h-4 w-4 animate-pulse" />
      <span>Calculating glucose patterns...</span>
    </div>
  </div>
{:else if error}
  <div class="flex h-full w-full items-center justify-center">
    <div class="text-center">
      <p class="text-sm font-medium text-destructive">Failed to load chart</p>
      <p class="mt-1 text-xs text-muted-foreground">{error}</p>
    </div>
  </div>
{:else if data.length > 0}
  <AreaChart
    {data}
    x={(d) => d.hour}
    y={(d) => d.median}
    renderContext="svg"
    legend
    series={[
      {
        key: "p10",
        value: [(d) => d.percentiles?.p25, (d) => d.percentiles?.p10],
        color: "var(--chart-1)",
        label: "P10",
      },
      {
        key: "p25",
        value: [(d) => d.median, (d) => d.percentiles?.p25],
        color: "var(--chart-2)",
        label: "P25",
      },
      {
        key: "median",
        value: [(d) => d.median, (d) => d.median],
        color: "black",
        props: {
          line: { strokeWidth: 1.75 },
        },
        label: "Median",
      },
      {
        key: "percentiles.p75",
        value: [(d) => d.median, (d) => d.percentiles?.p75],
        color: "var(--chart-3)",
        label: "P75",
      },
      {
        key: "p90",
        value: [(d) => d.percentiles?.p75, (d) => d.percentiles?.p90],
        color: "var(--chart-1)",
        label: "P90",
      },
    ]}
    xDomain={[0, 23]}
    {yDomain}
    seriesLayout="overlap"
    tooltip={{ mode: "bisect-x" }}
    annotations={[
      {
        type: "line",
        x: 0,
        y: lowThreshold,
        label: "Low",
        labelXOffset: 4,
        labelYOffset: 4,
        props: {
          label: {
            class: "text-xs text-muted-foreground",
          },
          line: {
            stroke: "var(--glucose-low)",
            strokeWidth: 1,
            "stroke-dasharray": "4 2",
          },
        },
      },
      {
        type: "line",
        x: 0,
        y: highThreshold,
        label: "High",
        labelXOffset: 4,
        labelYOffset: -12,
        props: {
          label: {
            class: "text-xs text-muted-foreground",
          },
          line: {
            stroke: "var(--glucose-high)",
            strokeWidth: 1,
            "stroke-dasharray": "4 2",
          },
        },
      },
    ]}
    brush
    props={{
      area: { motion: { type: "tween", duration: 200 } },
      xAxis: {
        motion: { type: "tween", duration: 200 },
        tickMultiline: true,
        format: formatHour,
      },
    }}
    padding={{ top: 20, right: 20, bottom: 40, left: 20 }}
  ></AreaChart>
{:else}
  <div
    class="flex h-full w-full items-center justify-center text-muted-foreground"
  >
    <div class="text-center">
      <BarChart3 class="mx-auto h-10 w-10 opacity-30" />
      <p class="mt-2 font-medium">No pattern data</p>
      <p class="text-sm">Need more readings to show your typical day</p>
    </div>
  </div>
{/if}
