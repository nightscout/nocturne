<script lang="ts">
  import { BarChart } from "layerchart";
  import { PieChart } from "lucide-svelte";
  import { patternClass } from "$lib/components/charts/print/chart-print-patterns";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";

  // Local type definitions matching the backend response structure
  interface DailyBasalBolusData {
    displayDate?: string;
    basal?: number;
    bolus?: number;
    total?: number;
  }

  interface DailyBasalBolusRatioResponse {
    dailyData?: DailyBasalBolusData[];
    averageBasalPercent?: number;
    averageBolusPercent?: number;
  }

  interface Props {
    /** Optional pre-loaded ratio data */
    data?: DailyBasalBolusRatioResponse | null;
    /** Whether data is currently loading */
    loading?: boolean;
  }

  let { data = null, loading = false }: Props = $props();

  // Extract data from prop
  const ratioData = $derived(data);
  const chartData = $derived(ratioData?.dailyData ?? []);
  const averageBasalPercent = $derived(ratioData?.averageBasalPercent ?? 0);
  const averageBolusPercent = $derived(ratioData?.averageBolusPercent ?? 0);

  const MAX_X_LABELS = 10;
  const xTicks = $derived.by(() => {
    const step = Math.ceil(chartData.length / MAX_X_LABELS);
    return chartData.filter((_, i) => i % step === 0).map((d) => d.displayDate);
  });
</script>

<div class="w-full">
  {#if loading}
    <div
      class="flex h-[350px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <div
          class="mx-auto h-10 w-10 animate-spin rounded-full border-4 border-primary border-t-transparent"
        ></div>
        <p class="mt-2 font-medium">Loading data...</p>
      </div>
    </div>
  {:else if chartData.length > 0 && chartData.some((d) => (d.total ?? 0) > 0)}
    <!-- Stacked Bar Chart -->
    <div class="h-75 w-full">
      <BarChart
        data={chartData}
        x="displayDate"
        series={[
          {
            key: "basal",
            color: "var(--insulin-scheduled-basal)",
            label: "Basal (U)",
            props: { class: patternClass("insulin-scheduled-basal") },
          },
          {
            key: "bolus",
            color: "var(--insulin-bolus)",
            label: "Bolus (U)",
            props: { class: patternClass("insulin-bolus") },
          },
        ]}
        seriesLayout="stack"
        tooltipContext={{ mode: "band" }}
        props={{
          xAxis: {
            ticks: xTicks,
          },
          yAxis: {
            label: "Insulin (U)",
          },
        }}
        padding={{ top: 20, right: 20, bottom: 30, left: 50 }}
      />
    </div>
    <ChartKey
      class="mt-2"
      items={[
        { texture: "insulin-scheduled-basal", label: "Basal (U)", color: "var(--insulin-scheduled-basal)" },
        { texture: "insulin-bolus", label: "Bolus (U)", color: "var(--insulin-bolus)" },
      ]}
    />

    <!-- Ideal ratio guidance -->
    <div class="mt-4 rounded-lg border border-dashed bg-muted/30 p-3">
      <p class="text-center text-sm text-muted-foreground">
        <strong>Typical ratios:</strong>
        Most people with Type 1 diabetes have around 50% basal and 50% bolus. Ratios
        can vary based on diet, activity level, and individual needs.
        {#if averageBasalPercent > 60}
          <span class="text-warning">
            Your basal percentage is higher than typical — consider discussing
            with your healthcare provider.
          </span>
        {:else if averageBolusPercent > 60}
          <span class="text-info">
            Your bolus percentage is higher than typical — this may indicate
            high carb meals or frequent corrections.
          </span>
        {/if}
      </p>
    </div>
  {:else}
    <div
      class="flex h-[350px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <PieChart class="mx-auto h-10 w-10 opacity-30" />
        <p class="mt-2 font-medium">No insulin data available</p>
        <p class="text-sm">No basal or bolus treatments found in this period</p>
      </div>
    </div>
  {/if}
</div>
