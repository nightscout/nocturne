<script lang="ts">
  import { BarChart } from "layerchart";
  import { PieChart } from "lucide-svelte";
  import { categoryPatternClass } from "$lib/components/charts/print/chart-print-patterns";

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
    /** Start date for the report (ISO string or Date) - for future data fetching */
    startDate?: string | Date;
    /** End date for the report (ISO string or Date) - for future data fetching */
    endDate?: string | Date;
    /** Optional pre-loaded ratio data */
    data?: DailyBasalBolusRatioResponse | null;
    /** Whether data is currently loading */
    loading?: boolean;
  }

  let {
    startDate: _startDate,
    endDate: _endDate,
    data = null,
    loading = false,
  }: Props = $props();

  // Extract data from prop
  const ratioData = $derived(data as DailyBasalBolusRatioResponse | null);
  const chartData = $derived(ratioData?.dailyData ?? []);
  const averageBasalPercent = $derived(ratioData?.averageBasalPercent ?? 0);
  const averageBolusPercent = $derived(ratioData?.averageBolusPercent ?? 0);
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
            props: { class: categoryPatternClass(1) },
          },
          {
            key: "bolus",
            color: "var(--insulin-bolus)",
            label: "Bolus (U)",
            props: { class: categoryPatternClass(3) },
          },
        ]}
        seriesLayout="stack"
        legend
        tooltip={{
          mode: "band",
        }}
        props={{
          xAxis: {
            tickMultiline: true,
          },
          yAxis: {
            label: "Insulin (U)",
          },
        }}
        padding={{ top: 20, right: 20, bottom: 50, left: 50 }}
      />
    </div>

    <!-- Ideal ratio guidance -->
    <div class="mt-4 rounded-lg border border-dashed bg-muted/30 p-3">
      <p class="text-center text-sm text-muted-foreground">
        <strong>Typical ratios:</strong>
        Most people with Type 1 diabetes have around 50% basal and 50% bolus. Ratios
        can vary based on diet, activity level, and individual needs.
        {#if averageBasalPercent > 60}
          <span class="text-amber-600 dark:text-amber-400">
            Your basal percentage is higher than typical — consider discussing
            with your healthcare provider.
          </span>
        {:else if averageBolusPercent > 60}
          <span class="text-blue-600 dark:text-blue-400">
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
