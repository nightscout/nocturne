<script lang="ts">
  import { BarChart } from "layerchart";
  import type { Treatment } from "$lib/api";
  import { PieChart } from "lucide-svelte";

  interface DailyRatioData {
    date: string;
    displayDate: string;
    basal: number;
    bolus: number;
    total: number;
    basalPercent: number;
    bolusPercent: number;
  }

  interface Props {
    treatments: Treatment[];
  }

  let { treatments }: Props = $props();

  // Check if a treatment is a bolus type
  function isBolusTreatment(treatment: Treatment): boolean {
    // Primary check: has insulin and not a basal type
    if (treatment.insulin !== undefined && treatment.insulin > 0) {
      const eventType = treatment.eventType?.toLowerCase() || "";
      // Only exclude if explicitly basal
      if (!eventType.includes("basal") && !eventType.includes("temp")) {
        return true;
      }
    }
    // Secondary check: eventType indicates bolus
    const eventType = treatment.eventType?.toLowerCase() || "";
    return (
      eventType.includes("bolus") ||
      eventType.includes("correction") ||
      eventType.includes("smb")
    );
  }

  // Check if a treatment is a basal type
  function isBasalTreatment(treatment: Treatment): boolean {
    const eventType = treatment.eventType?.toLowerCase() || "";
    return (
      eventType.includes("basal") ||
      eventType === "tempbasal" ||
      eventType === "temp basal" ||
      treatment.rate !== undefined ||
      treatment.absolute !== undefined
    );
  }

  // Calculate basal insulin delivered from a temp basal treatment
  function getBasalInsulin(treatment: Treatment): number {
    // For temp basals, calculate insulin based on rate and duration
    const rate = treatment.rate ?? treatment.absolute ?? 0;
    const duration = treatment.duration ?? 0;
    if (rate > 0 && duration > 0) {
      return (rate * duration) / 60; // Convert rate (U/hr) * duration (min) to units
    }
    return 0;
  }

  // Process treatments to get daily basal/bolus ratios
  function processTreatments(treatments: Treatment[]): DailyRatioData[] {
    const dailyData: Map<string, { basal: number; bolus: number }> = new Map();

    for (const treatment of treatments) {
      const date = new Date(
        treatment.created_at ??
          treatment.eventTime ??
          treatment.mills ??
          Date.now()
      );
      if (isNaN(date.getTime())) continue;

      const dateKey = date.toISOString().split("T")[0];

      if (!dailyData.has(dateKey)) {
        dailyData.set(dateKey, { basal: 0, bolus: 0 });
      }

      const dayData = dailyData.get(dateKey)!;

      if (isBolusTreatment(treatment)) {
        dayData.bolus += treatment.insulin ?? 0;
      } else if (isBasalTreatment(treatment)) {
        dayData.basal += getBasalInsulin(treatment);
      }
    }

    // Convert to array and sort by date
    const result: DailyRatioData[] = [];
    const sortedDates = Array.from(dailyData.keys()).sort();

    for (const dateKey of sortedDates) {
      const dayData = dailyData.get(dateKey)!;
      const total = dayData.basal + dayData.bolus;
      const basalPercent = total > 0 ? (dayData.basal / total) * 100 : 0;
      const bolusPercent = total > 0 ? (dayData.bolus / total) * 100 : 0;

      result.push({
        date: dateKey,
        displayDate: new Date(dateKey).toLocaleDateString("en-US", {
          month: "short",
          day: "numeric",
        }),
        basal: dayData.basal,
        bolus: dayData.bolus,
        total,
        basalPercent,
        bolusPercent,
      });
    }

    return result;
  }

  // Use derived values instead of effect to avoid infinite loops
  const chartData = $derived(
    treatments && treatments.length > 0 ? processTreatments(treatments) : []
  );

  const averageBasalPercent = $derived.by(() => {
    if (chartData.length === 0) return 0;
    const totalBasal = chartData.reduce((sum, d) => sum + d.basal, 0);
    const totalBolus = chartData.reduce((sum, d) => sum + d.bolus, 0);
    const grandTotal = totalBasal + totalBolus;
    return grandTotal > 0 ? (totalBasal / grandTotal) * 100 : 0;
  });

  const averageBolusPercent = $derived.by(() => {
    if (chartData.length === 0) return 0;
    const totalBasal = chartData.reduce((sum, d) => sum + d.basal, 0);
    const totalBolus = chartData.reduce((sum, d) => sum + d.bolus, 0);
    const grandTotal = totalBasal + totalBolus;
    return grandTotal > 0 ? (totalBolus / grandTotal) * 100 : 0;
  });
</script>

<div class="w-full">
  {#if chartData.length > 0 && chartData.some((d) => d.total > 0)}
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
          {(
            chartData.reduce((sum, d) => sum + d.total, 0) / chartData.length
          ).toFixed(1)}U
        </div>
        <div class="text-xs font-medium text-muted-foreground">Avg TDD</div>
      </div>
      <div class="rounded-lg border bg-card p-4 text-center">
        <div class="text-2xl font-bold">{chartData.length}</div>
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
