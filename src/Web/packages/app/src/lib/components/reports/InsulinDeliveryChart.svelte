<script lang="ts">
  import { AreaChart } from "layerchart";
  import type { HourlyInsulinDeliveryPoint } from "$lib/api";
  import { Syringe } from "lucide-svelte";
  import { categoryPatternClass } from "$lib/components/charts/print/chart-print-patterns";

  interface Props {
    /** Backend-computed hourly delivery averages (24 entries, hour 0-23) */
    data: HourlyInsulinDeliveryPoint[];
    showStacked?: boolean;
  }

  let { data, showStacked = true }: Props = $props();

  // Format hour for display
  function formatHour(hour: number): string {
    if (hour === 0) return "12 AM";
    if (hour < 12) return `${hour} AM`;
    if (hour === 12) return "12 PM";
    return `${hour - 12} PM`;
  }

  const chartData = $derived(data ?? []);

  // The non-stacked variant plots basal only (used by the basal analysis
  // report); the stacked variant plots the full delivery split.
  const displayValue = $derived(
    showStacked
      ? (d: HourlyInsulinDeliveryPoint) => d.total ?? 0
      : (d: HourlyInsulinDeliveryPoint) => d.basal ?? 0
  );

  const maxInsulin = $derived.by(() => {
    if (chartData.length === 0) return 5;
    const maxValue = Math.max(...chartData.map(displayValue));
    return Math.max(2, Math.ceil(maxValue * 1.2));
  });

  // Check if we have both scheduled and temp basal data
  const hasScheduledBasalData = $derived(
    chartData.some((d) => (d.scheduledBasal ?? 0) > 0)
  );
  const hasTempBasalData = $derived(chartData.some((d) => (d.tempBasal ?? 0) > 0));
</script>

<div class="w-full">
  {#if chartData.length > 0 && chartData.some((d) => displayValue(d) > 0)}
    <div class="h-[350px] w-full">
      <AreaChart
        data={chartData}
        x={(d) => d.hour}
        y={displayValue}
        series={showStacked
          ? [
              // Show scheduled basal, temp basal adjustments, and bolus as stacked
              ...(hasScheduledBasalData
                ? [
                    {
                      key: "scheduledBasal",
                      value: (d: HourlyInsulinDeliveryPoint) => d.scheduledBasal ?? 0,
                      color: "var(--insulin-scheduled-basal)",
                      label: "Scheduled Basal",
                      props: { class: categoryPatternClass(1) },
                    },
                  ]
                : []),
              ...(hasTempBasalData
                ? [
                    {
                      key: "tempBasal",
                      value: (d: HourlyInsulinDeliveryPoint) => d.tempBasal ?? 0,
                      color: "var(--insulin-additional-basal)",
                      label: "Temp Basal",
                      props: { class: categoryPatternClass(2) },
                    },
                  ]
                : []),
              // Fallback if no scheduled/temp distinction - show combined basal
              ...(!hasScheduledBasalData && !hasTempBasalData
                ? [
                    {
                      key: "basal",
                      value: (d: HourlyInsulinDeliveryPoint) => d.basal ?? 0,
                      color: "var(--insulin-scheduled-basal)",
                      label: "Basal",
                      props: { class: categoryPatternClass(1) },
                    },
                  ]
                : []),
              {
                key: "bolus",
                value: (d: HourlyInsulinDeliveryPoint) => d.bolus ?? 0,
                color: "var(--insulin-bolus)",
                label: "Bolus",
                props: { class: categoryPatternClass(3) },
              },
            ]
          : [
              {
                key: "basal",
                value: (d: HourlyInsulinDeliveryPoint) => d.basal ?? 0,
                color: "var(--chart-1)",
                label: "Basal Insulin",
              },
            ]}
        xDomain={[0, 23]}
        yDomain={[0, maxInsulin]}
        seriesLayout={showStacked ? "stack" : "overlap"}
        tooltipContext={{ mode: "bisect-x" }}
        props={{
          xAxis: {
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
      {@const morning = chartData.slice(6, 12).reduce((s, d) => s + displayValue(d), 0)}
      {@const afternoon = chartData
        .slice(12, 18)
        .reduce((s, d) => s + displayValue(d), 0)}
      {@const evening =
        chartData.slice(18, 24).reduce((s, d) => s + displayValue(d), 0) +
        chartData.slice(0, 6).reduce((s, d) => s + displayValue(d), 0)}
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
        <p class="mt-2 font-medium">
          {showStacked ? "No insulin delivery data" : "No basal delivery data"}
        </p>
        <p class="text-sm">
          {showStacked
            ? "No treatments found in this period"
            : "No basal records found in this period"}
        </p>
      </div>
    </div>
  {/if}
</div>
