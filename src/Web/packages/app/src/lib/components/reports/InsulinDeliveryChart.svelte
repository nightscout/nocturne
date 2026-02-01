<script lang="ts">
  import { AreaChart } from "layerchart";
  import type { Treatment, BasalPoint } from "$lib/api";
  import { BasalDeliveryOrigin } from "$lib/api";
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
    basalSeries?: BasalPoint[];
    showStacked?: boolean;
  }

  let { treatments, basalSeries = [], showStacked = true }: Props = $props();

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

  // Format hour for display
  function formatHour(hour: number): string {
    if (hour === 0) return "12 AM";
    if (hour < 12) return `${hour} AM`;
    if (hour === 12) return "12 PM";
    return `${hour - 12} PM`;
  }

  // Process treatments and basal series to get hourly insulin delivery
  function processInsulinData(treatments: Treatment[], basalPoints: BasalPoint[]): HourlyInsulinData[] {
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

    // Process bolus treatments
    for (const treatment of treatments) {
      const date = new Date(
        treatment.created_at ??
          treatment.eventTime ??
          treatment.mills ??
          Date.now()
      );
      if (isNaN(date.getTime())) continue;

      uniqueDays.add(date.toISOString().split("T")[0]);

      if (isBolusTreatment(treatment)) {
        const hour = date.getHours();
        const hourData = hourlyData.get(hour)!;
        hourData.bolus += treatment.insulin ?? 0;
        hourData.count++;
      }
    }

    // Process basal series from chart data API
    // Each BasalPoint represents a rate at a timestamp - we need to calculate insulin delivered
    const sortedBasal = [...basalPoints].sort((a, b) => (a.timestamp ?? 0) - (b.timestamp ?? 0));

    for (let i = 0; i < sortedBasal.length; i++) {
      const point = sortedBasal[i];
      const timestamp = point.timestamp ?? 0;
      const rate = point.rate ?? 0;
      const origin = point.origin;

      // Calculate duration until next point (or assume 5 min if last point)
      const nextTimestamp = sortedBasal[i + 1]?.timestamp ?? (timestamp + 5 * 60 * 1000);
      const durationMs = nextTimestamp - timestamp;
      const durationHours = durationMs / (1000 * 60 * 60);

      // Calculate insulin delivered during this period
      const insulinDelivered = rate * durationHours;

      if (insulinDelivered <= 0) continue;

      const date = new Date(timestamp);
      if (isNaN(date.getTime())) continue;

      uniqueDays.add(date.toISOString().split("T")[0]);
      const hour = date.getHours();
      const hourData = hourlyData.get(hour)!;

      // Categorize by origin
      if (origin === BasalDeliveryOrigin.Scheduled || origin === BasalDeliveryOrigin.Inferred) {
        hourData.scheduledBasal += insulinDelivered;
      } else {
        // Algorithm, Manual, Suspended all count as "temp" adjustments
        hourData.tempBasal += insulinDelivered;
      }
      hourData.count++;
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
    (treatments && treatments.length > 0) || (basalSeries && basalSeries.length > 0)
      ? processInsulinData(treatments, basalSeries)
      : []
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
                color: "var(--chart-1)",
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
