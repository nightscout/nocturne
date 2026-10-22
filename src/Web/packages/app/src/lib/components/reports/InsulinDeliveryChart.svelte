<script lang="ts">
  import { AreaChart, Spline, Tooltip } from "layerchart";
  import { curveStep } from "d3";
  import type { HourlyInsulinDeliveryPoint, HourlyInsulinDeliveryResponse } from "$lib/api";
  import Syringe from "@lucide/svelte/icons/syringe";
  import {
    dashOf,
    patternClass,
    type TextureKey,
  } from "$lib/components/charts/print/chart-print-patterns";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";

  interface Props {
    /** Backend-computed hourly delivery averages (24 entries, hour 0-23) and period totals */
    delivery?: HourlyInsulinDeliveryResponse;
    showStacked?: boolean;
  }

  let { delivery, showStacked = true }: Props = $props();

  function formatHour(hour: number): string {
    if (hour === 0) return "12 AM";
    if (hour < 12) return `${hour} AM`;
    if (hour === 12) return "12 PM";
    return `${hour - 12} PM`;
  }

  const chartData = $derived(delivery?.hours ?? []);
  const periods = $derived(showStacked ? delivery?.totalDeliveryPeriods : delivery?.basalPeriods);

  // The non-stacked variant plots basal only (used by the basal analysis
  // report); the stacked variant plots the full delivery split.
  const displayValue = $derived(
    showStacked
      ? (d: HourlyInsulinDeliveryPoint) => d.total ?? 0
      : (d: HourlyInsulinDeliveryPoint) => d.basal ?? 0
  );

  const maxInsulin = $derived.by(() => {
    if (chartData.length === 0) return 5;
    const maxValue = Math.max(
      ...chartData.map(displayValue),
      ...(showStacked ? chartData.map((d) => d.profileBasalRate ?? 0) : [])
    );
    return Math.max(2, Math.ceil(maxValue * 1.2));
  });

  const hasScheduledBasalData = $derived(
    chartData.some((d) => (d.scheduledBasal ?? 0) > 0)
  );
  const hasProfileBasalRate = $derived(
    showStacked && chartData.some((d) => (d.profileBasalRate ?? 0) > 0)
  );
  const hasTempBasalData = $derived(chartData.some((d) => (d.tempBasal ?? 0) > 0));
  const hasAutomaticBolusData = $derived(
    chartData.some((d) => (d.automaticBolus ?? 0) > 0)
  );

  type Series = {
    key: string;
    value: (d: HourlyInsulinDeliveryPoint) => number;
    color: string;
    label: string;
    texture: TextureKey;
  };

  const stackedSeries = $derived.by((): Series[] => {
    const basal: Series[] = [];
    if (hasScheduledBasalData)
      basal.push({
        key: "scheduledBasal",
        value: (d) => d.scheduledBasal ?? 0,
        color: "var(--insulin-scheduled-basal)",
        label: "Scheduled Basal",
        texture: "insulin-scheduled-basal",
      });
    if (hasTempBasalData)
      basal.push({
        key: "tempBasal",
        value: (d) => d.tempBasal ?? 0,
        color: "var(--insulin-additional-basal)",
        label: "Temp Basal",
        texture: "insulin-temp-basal",
      });
    if (basal.length === 0)
      basal.push({
        key: "basal",
        value: (d) => d.basal ?? 0,
        color: "var(--basal)",
        label: "Basal",
        texture: "insulin-scheduled-basal",
      });
    const bolus: Series[] = [
      {
        key: "manualBolus",
        value: (d) => d.manualBolus ?? 0,
        color: "var(--insulin-bolus)",
        label: "Manual Bolus",
        texture: "insulin-bolus",
      },
    ];
    if (hasAutomaticBolusData)
      bolus.push({
        key: "automaticBolus",
        value: (d) => d.automaticBolus ?? 0,
        color: "var(--iob-temporary)",
        label: "Automatic Bolus",
        texture: "insulin-automatic-bolus",
      });
    return [...basal, ...bolus];
  });

  const series = $derived(
    showStacked
      ? stackedSeries.map(({ texture, ...s }) => ({
          ...s,
          props: { class: patternClass(texture) },
        }))
      : [
          {
            key: "basal",
            value: (d: HourlyInsulinDeliveryPoint) => d.basal ?? 0,
            color: "var(--basal)",
            label: "Basal Insulin",
          },
        ]
  );
</script>

<div class="w-full">
  {#if chartData.length > 0 && chartData.some((d) => displayValue(d) > 0)}
    <div class="h-[350px] w-full">
      <AreaChart
        data={chartData}
        x={(d) => d.hour}
        y={displayValue}
        {series}
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
      >
        {#snippet tooltip({ context })}
          <Tooltip.Root {context}>
            {#snippet children({ data: point }: { data: HourlyInsulinDeliveryPoint })}
              <Tooltip.Header value={formatHour(point.hour ?? 0)} />
              <Tooltip.List>
                {#each series as s (s.key)}
                  <Tooltip.Item
                    label={s.label}
                    value={`${s.value(point).toFixed(2)} U`}
                    color={s.color}
                    valueAlign="right"
                  />
                {/each}
                {#if showStacked}
                  <Tooltip.Item label="Total" value={`${(point.total ?? 0).toFixed(2)} U`} valueAlign="right" />
                  {#if hasProfileBasalRate}
                    <Tooltip.Item
                      label="Scheduled basal rate"
                      value={`${(point.profileBasalRate ?? 0).toFixed(2)} U/h`}
                      color="var(--basal)"
                      valueAlign="right"
                    />
                  {/if}
                {/if}
              </Tooltip.List>
            {/snippet}
          </Tooltip.Root>
        {/snippet}
        {#snippet aboveMarks()}
          {#if hasProfileBasalRate}
            <Spline
              data={chartData}
              x={(d) => d.hour}
              y={(d) => d.profileBasalRate ?? 0}
              curve={curveStep}
              class="stroke-basal stroke-2 fill-none"
              stroke-dasharray={dashOf("scheduled-basal-rate")}
            />
          {/if}
        {/snippet}
      </AreaChart>
    </div>
    {#if showStacked}
      <ChartKey
        class="mt-2"
        items={[
          ...stackedSeries.map((s) => ({ texture: s.texture, label: s.label, color: s.color })),
          ...(hasProfileBasalRate
            ? [{ texture: "scheduled-basal-rate" as const, label: "Scheduled basal rate", shape: "line" as const }]
            : []),
        ]}
      />
    {/if}

    {#if periods}
      {@const blocks = [
        { label: "Morning (6am-12pm)", ...periods.morning },
        { label: "Afternoon (12pm-6pm)", ...periods.afternoon },
        { label: "Evening/Night (6pm-6am)", ...periods.eveningNight },
      ]}
      <dl class="m-0 mt-4 grid grid-cols-3 divide-x divide-border border-t border-border">
        {#each blocks as block (block.label)}
          <div class="px-3 pt-3 first:pl-0 last:pr-0">
            <dt class="text-xs text-muted-foreground">{block.label}</dt>
            <dd class="m-0 mt-1 flex flex-wrap items-baseline gap-x-1">
              <span class="text-lg font-semibold tabular-nums">{(block.units ?? 0).toFixed(2)}</span>
              <span class="text-xs text-muted-foreground">U</span>
              <span class="ml-1 text-xs text-muted-foreground tabular-nums">
                {(block.percent ?? 0).toFixed(0)}%
              </span>
            </dd>
          </div>
        {/each}
      </dl>
      <p class="m-0 mt-2 text-xs text-muted-foreground tabular-nums">
        Daily total {(periods.dailyTotal ?? 0).toFixed(2)} U
      </p>
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
