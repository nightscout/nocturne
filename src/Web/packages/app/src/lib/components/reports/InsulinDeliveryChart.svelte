<script lang="ts">
  import { AreaChart } from "layerchart";
  import type { Treatment } from "$lib/api";
  import { Syringe } from "lucide-svelte";

  interface HourlyInsulinData {
    hour: number;
    timeLabel: string;
    scheduledBasal: number;
    tempBasal: number;
    basal: number; // Total basal (scheduled + temp adjustment)
    bolus: number;
    total: number;
    count: number;
  }

  interface Props {
    treatments: Treatment[];
    showStacked?: boolean;
  }

  let { treatments, showStacked = true }: Props = $props();

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

  // Check if a treatment is a scheduled basal
  function isScheduledBasal(treatment: Treatment): boolean {
    const eventType = treatment.eventType?.toLowerCase() || "";
    return eventType === "scheduled basal" || eventType === "scheduledbasal";
  }

  // Check if a treatment is a temp basal
  function isTempBasal(treatment: Treatment): boolean {
    const eventType = treatment.eventType?.toLowerCase() || "";
    return (
      eventType.includes("temp") ||
      eventType === "tempbasal" ||
      (eventType.includes("basal") && !isScheduledBasal(treatment))
    );
  }

  // Check if a treatment is any basal type
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

  // Calculate basal insulin delivered from a basal treatment
  function getBasalInsulin(treatment: Treatment): number {
    const rate = treatment.rate ?? treatment.absolute ?? 0;
    const duration = treatment.duration ?? 0;
    if (rate > 0 && duration > 0) {
      return (rate * duration) / 60;
    }
    return 0;
  }

  // Format hour for display
  function formatHour(hour: number): string {
    if (hour === 0) return "12 AM";
    if (hour < 12) return `${hour} AM`;
    if (hour === 12) return "12 PM";
    return `${hour - 12} PM`;
  }

  // Process treatments to get hourly insulin delivery
  function processInsulinData(treatments: Treatment[]): HourlyInsulinData[] {
    // Initialize hourly buckets
    const hourlyData: Map<
      number,
      {
        scheduledBasal: number;
        tempBasal: number;
        bolus: number;
        count: number;
      }
    > = new Map();
    for (let h = 0; h < 24; h++) {
      hourlyData.set(h, {
        scheduledBasal: 0,
        tempBasal: 0,
        bolus: 0,
        count: 0,
      });
    }

    // Count number of days for averaging
    const uniqueDays = new Set<string>();

    for (const treatment of treatments) {
      const date = new Date(
        treatment.created_at ??
          treatment.eventTime ??
          treatment.mills ??
          Date.now()
      );
      if (isNaN(date.getTime())) continue;

      uniqueDays.add(date.toISOString().split("T")[0]);
      const hour = date.getHours();
      const hourData = hourlyData.get(hour)!;

      if (isBolusTreatment(treatment)) {
        hourData.bolus += treatment.insulin ?? 0;
        hourData.count++;
      } else if (isScheduledBasal(treatment)) {
        hourData.scheduledBasal += getBasalInsulin(treatment);
        hourData.count++;
      } else if (isTempBasal(treatment)) {
        hourData.tempBasal += getBasalInsulin(treatment);
        hourData.count++;
      } else if (isBasalTreatment(treatment)) {
        // Fallback for unknown basal types - add to temp basal
        hourData.tempBasal += getBasalInsulin(treatment);
        hourData.count++;
      }
    }

    const numDays = Math.max(1, uniqueDays.size);

    // Convert to array with averaged values
    const data: HourlyInsulinData[] = [];
    for (let hour = 0; hour < 24; hour++) {
      const hourData = hourlyData.get(hour)!;
      const avgScheduledBasal = hourData.scheduledBasal / numDays;
      const avgTempBasal = hourData.tempBasal / numDays;
      const avgBolus = hourData.bolus / numDays;
      const avgBasal = avgScheduledBasal + avgTempBasal;
      data.push({
        hour,
        timeLabel: formatHour(hour),
        scheduledBasal: Math.round(avgScheduledBasal * 100) / 100,
        tempBasal: Math.round(avgTempBasal * 100) / 100,
        basal: Math.round(avgBasal * 100) / 100,
        bolus: Math.round(avgBolus * 100) / 100,
        total: Math.round((avgBasal + avgBolus) * 100) / 100,
        count: hourData.count,
      });
    }

    return data;
  }

  // Use derived values instead of effect to avoid infinite loops
  const chartData = $derived(
    treatments && treatments.length > 0 ? processInsulinData(treatments) : []
  );

  const maxInsulin = $derived.by(() => {
    if (chartData.length === 0) return 5;
    const maxTotal = Math.max(...chartData.map((d) => d.total));
    return Math.max(2, Math.ceil(maxTotal * 1.2));
  });

  // Check if we have both scheduled and temp basal data
  const hasScheduledBasalData = $derived(
    chartData.some((d) => d.scheduledBasal > 0)
  );
  const hasTempBasalData = $derived(chartData.some((d) => d.tempBasal > 0));
</script>

<div class="w-full">
  {#if chartData.length > 0 && chartData.some((d) => d.total > 0)}
    <div class="h-[350px] w-full">
      <AreaChart
        data={chartData}
        x={(d) => d.hour}
        y={(d) => d.total}
        renderContext="svg"
        legend
        series={showStacked
          ? [
              // Show scheduled basal, temp basal adjustments, and bolus as stacked
              ...(hasScheduledBasalData
                ? [
                    {
                      key: "scheduledBasal",
                      value: (d: HourlyInsulinData) => d.scheduledBasal,
                      color: "hsl(38, 70%, 45%)",
                      label: "Scheduled Basal",
                    },
                  ]
                : []),
              ...(hasTempBasalData
                ? [
                    {
                      key: "tempBasal",
                      value: (d: HourlyInsulinData) => d.tempBasal,
                      color: "hsl(38, 92%, 60%)",
                      label: "Temp Basal",
                    },
                  ]
                : []),
              // Fallback if no scheduled/temp distinction - show combined basal
              ...(!hasScheduledBasalData && !hasTempBasalData
                ? [
                    {
                      key: "basal",
                      value: (d: HourlyInsulinData) => d.basal,
                      color: "hsl(38, 92%, 50%)",
                      label: "Basal",
                    },
                  ]
                : []),
              {
                key: "bolus",
                value: (d: HourlyInsulinData) => d.bolus,
                color: "hsl(217, 91%, 60%)",
                label: "Bolus",
              },
            ]
          : [
              {
                key: "total",
                value: (d: HourlyInsulinData) => d.total,
                color: "hsl(var(--primary))",
                label: "Total Insulin",
              },
            ]}
        xDomain={[0, 23]}
        yDomain={[0, maxInsulin]}
        seriesLayout={showStacked ? "stack" : "overlap"}
        tooltip={{ mode: "bisect-x" }}
        props={{
          area: { motion: { type: "tween", duration: 200 } },
          xAxis: {
            motion: { type: "tween", duration: 200 },
            format: formatHour,
          },
          yAxis: {
            label: "Avg Insulin (U)",
          },
        }}
        padding={{ top: 20, right: 20, bottom: 40, left: 50 }}
      />
    </div>

    <!-- Time period insights -->
    {#if chartData.length >= 24}
      {@const morning = chartData.slice(6, 12).reduce((s, d) => s + d.total, 0)}
      {@const afternoon = chartData
        .slice(12, 18)
        .reduce((s, d) => s + d.total, 0)}
      {@const evening =
        chartData.slice(18, 24).reduce((s, d) => s + d.total, 0) +
        chartData.slice(0, 6).reduce((s, d) => s + d.total, 0)}
      {@const totalDaily = morning + afternoon + evening}
      <div class="mt-4 grid grid-cols-3 gap-3 text-center">
        <div class="rounded-lg border bg-card p-3">
          <div class="text-lg font-bold">{morning.toFixed(1)}U</div>
          <div class="text-xs text-muted-foreground">Morning (6am-12pm)</div>
          <div class="text-xs font-medium text-amber-600">
            {totalDaily > 0 ? ((morning / totalDaily) * 100).toFixed(0) : 0}%
          </div>
        </div>
        <div class="rounded-lg border bg-card p-3">
          <div class="text-lg font-bold">{afternoon.toFixed(1)}U</div>
          <div class="text-xs text-muted-foreground">Afternoon (12pm-6pm)</div>
          <div class="text-xs font-medium text-blue-600">
            {totalDaily > 0 ? ((afternoon / totalDaily) * 100).toFixed(0) : 0}%
          </div>
        </div>
        <div class="rounded-lg border bg-card p-3">
          <div class="text-lg font-bold">{evening.toFixed(1)}U</div>
          <div class="text-xs text-muted-foreground">Evening/Night</div>
          <div class="text-xs font-medium text-purple-600">
            {totalDaily > 0 ? ((evening / totalDaily) * 100).toFixed(0) : 0}%
          </div>
        </div>
      </div>
    {/if}
  {:else}
    <div
      class="flex h-[350px] w-full items-center justify-center text-muted-foreground"
    >
      <div class="text-center">
        <Syringe class="mx-auto h-10 w-10 opacity-30" />
        <p class="mt-2 font-medium">No insulin delivery data</p>
        <p class="text-sm">No treatments found in this period</p>
      </div>
    </div>
  {/if}
</div>
