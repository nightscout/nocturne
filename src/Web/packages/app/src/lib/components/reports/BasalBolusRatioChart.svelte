<script lang="ts">
  import { BarChart } from "layerchart";
  import type { DailyBasalBolusRatioResponse } from "$lib/api";
  import { getDailyBasalBolusRatios } from "$lib/data/statistics.remote";
  import { PieChart } from "lucide-svelte";
  import { resource } from "runed";

  interface Props {
    /** Start date for fetching data (ISO string or Date) */
    startDate: string | Date;
    /** End date for fetching data (ISO string or Date) */
    endDate: string | Date;
  }

  let { startDate, endDate }: Props = $props();

  // Normalize dates to ISO strings
  const startDateString = $derived(
    typeof startDate === "string" ? startDate : startDate.toISOString()
  );
  const endDateString = $derived(
    typeof endDate === "string" ? endDate : endDate.toISOString()
  );

  // Fetch data from backend
  const ratioResource = resource(
    () => ({ startDate: startDateString, endDate: endDateString }),
    async ({ startDate, endDate }) => {
      return await getDailyBasalBolusRatios({ startDate, endDate });
    },
    { debounce: 100 }
  );

  // Extract data from resource
  const ratioData = $derived(ratioResource.current as DailyBasalBolusRatioResponse | null);
  const chartData = $derived(ratioData?.dailyData ?? []);
  const averageBasalPercent = $derived(ratioData?.averageBasalPercent ?? 0);
  const averageBolusPercent = $derived(ratioData?.averageBolusPercent ?? 0);
  const averageTdd = $derived(ratioData?.averageTdd ?? 0);
  const dayCount = $derived(ratioData?.dayCount ?? 0);
</script>

<div class="w-full">
  {#if ratioResource.loading}
    <div
      class="flex h-[350px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <div class="mx-auto h-10 w-10 animate-spin rounded-full border-4 border-primary border-t-transparent"></div>
        <p class="mt-2 font-medium">Loading data...</p>
      </div>
    </div>
  {:else if chartData.length > 0 && chartData.some((d) => (d.total ?? 0) > 0)}
    <!-- Summary Cards -->
    <div class="mb-6 grid grid-cols-2 gap-4 md:grid-cols-4">
      <div class="rounded-lg border bg-card p-4 text-center">
        <div class="text-2xl font-bold text-amber-600 dark:text-amber-400">
          {averageBasalPercent.toFixed(0)}%
        </div>
        <div class="text-xs font-medium text-muted-foreground">Avg Basal</div>
      </div>
      <div class="rounded-lg border bg-card p-4 text-center">
        <div class="text-2xl font-bold text-blue-600 dark:text-blue-400">
          {averageBolusPercent.toFixed(0)}%
        </div>
        <div class="text-xs font-medium text-muted-foreground">Avg Bolus</div>
      </div>
      <div class="rounded-lg border bg-card p-4 text-center">
        <div class="text-2xl font-bold">
          {averageTdd.toFixed(1)}U
        </div>
        <div class="text-xs font-medium text-muted-foreground">Avg TDD</div>
      </div>
      <div class="rounded-lg border bg-card p-4 text-center">
        <div class="text-2xl font-bold">{dayCount}</div>
        <div class="text-xs font-medium text-muted-foreground">Days</div>
      </div>
    </div>

    <!-- Stacked Bar Chart -->
    <div class="h-[300px] w-full">
      <BarChart
        data={chartData}
        x="displayDate"
        series={[
          {
            key: "basal",
            color: "hsl(38, 92%, 50%)",
            label: "Basal (U)",
          },
          {
            key: "bolus",
            color: "hsl(217, 91%, 60%)",
            label: "Bolus (U)",
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
        Most people with Type 1 diabetes have around 50% basal and 50% bolus.
        Ratios can vary based on diet, activity level, and individual needs.
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
