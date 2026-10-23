<script lang="ts">
  import { Axis, BarChart, Points, Spline, Tooltip } from "layerchart";
  import { scaleLinear } from "d3-scale";
  import { carbAxisMax as niceCarbAxisMax, carbsOnInsulinScale } from "./carb-axis";
  import PieChart from "@lucide/svelte/icons/chart-pie";
  import { dashClass, patternClass } from "$lib/components/charts/print/chart-print-patterns";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import type { DailyBasalBolusRatioData, DailyBasalBolusRatioResponse } from "$lib/api";

  interface Props {
    /** Optional pre-loaded ratio data */
    data?: DailyBasalBolusRatioResponse | null;
    /** Whether data is currently loading */
    loading?: boolean;
  }

  let { data = null, loading = false }: Props = $props();

  const ratioData = $derived(data);
  const chartData = $derived(ratioData?.dailyData ?? []);

  const MAX_X_LABELS = 10;
  const xTicks = $derived.by(() => {
    const step = Math.ceil(chartData.length / MAX_X_LABELS);
    return chartData.filter((_, i) => i % step === 0).map((d) => d.displayDate);
  });

  // Carbs share the plot on their own right-hand axis, which starts at zero like the insulin
  // axis. The line is drawn through the insulin scale, so each day's grams are rescaled into
  // insulin units: the top of the carb axis lands on the top of the insulin axis.
  const hasCarbs = $derived(chartData.some((d) => (d.carbs ?? 0) > 0));
  const carbAxisMax = $derived(niceCarbAxisMax(chartData.map((d) => d.carbs ?? 0)));
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
    <div class="h-75 w-full">
      <BarChart
        data={chartData}
        x="displayDate"
        series={[
          {
            key: "basal",
            color: "var(--basal)",
            label: "Basal (U)",
            props: { class: patternClass("insulin-scheduled-basal") },
          },
          {
            key: "manualBolus",
            color: "var(--insulin-bolus)",
            label: "Manual bolus (U)",
            props: { class: patternClass("insulin-bolus") },
          },
          {
            key: "automaticBolus",
            color: "var(--iob-temporary)",
            label: "Automatic bolus (U)",
            props: { class: patternClass("insulin-automatic-bolus") },
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
        padding={{ top: 20, right: hasCarbs ? 50 : 20, bottom: 30, left: 50 }}
      >
        {#snippet aboveMarks({ context })}
          {#if hasCarbs}
            {@const yMax = Number(context.yScale.domain()[1])}
            {@const toInsulinScale = (d: DailyBasalBolusRatioData) =>
              carbsOnInsulinScale(d.carbs ?? 0, carbAxisMax, yMax)}
            <Spline
              data={chartData}
              x="displayDate"
              y={toInsulinScale}
              class="fill-none stroke-carbs stroke-2 {dashClass('carbs')}"
            />
            <Points
              data={chartData}
              x="displayDate"
              y={toInsulinScale}
              r={3}
              class="fill-carbs stroke-none"
            />
            <Axis
              placement="right"
              scale={scaleLinear().domain([0, carbAxisMax]).range(context.yScale.range())}
              label="Carbs (g)"
            />
          {/if}
        {/snippet}
        {#snippet tooltip({ context })}
          <Tooltip.Root {context}>
            {#snippet children({ data })}
              {@const d: DailyBasalBolusRatioData = data}
              <Tooltip.Header value={d.displayDate} />
              <Tooltip.List>
                <Tooltip.Item
                  label="Manual bolus (U)"
                  value={(d.manualBolus ?? 0).toFixed(1)}
                  color="var(--insulin-bolus)"
                  valueAlign="right"
                />
                <Tooltip.Item
                  label="Automatic bolus (U)"
                  value={(d.automaticBolus ?? 0).toFixed(1)}
                  color="var(--iob-temporary)"
                  valueAlign="right"
                />
                <Tooltip.Item
                  label="Basal (U)"
                  value={(d.basal ?? 0).toFixed(1)}
                  color="var(--basal)"
                  valueAlign="right"
                />
                {#if hasCarbs}
                  <Tooltip.Item
                    label="Carbs (g)"
                    value={(d.carbs ?? 0).toFixed(0)}
                    color="var(--carbs)"
                    valueAlign="right"
                  />
                {/if}
                <Tooltip.Separator />
                <Tooltip.Item
                  label="Total (U)"
                  value={(d.total ?? 0).toFixed(1)}
                  valueAlign="right"
                />
              </Tooltip.List>
            {/snippet}
          </Tooltip.Root>
        {/snippet}
      </BarChart>
    </div>
    <ChartKey
      class="mt-2"
      items={[
        { texture: "insulin-scheduled-basal", label: "Basal (U)", color: "var(--insulin-scheduled-basal)" },
        { texture: "insulin-bolus", label: "Manual bolus (U)", color: "var(--insulin-bolus)" },
        { texture: "insulin-automatic-bolus", label: "Automatic bolus (U)", color: "var(--iob-temporary)" },
        ...(hasCarbs
          ? [{ texture: "carbs" as const, label: "Carbs (g)", color: "var(--carbs)", shape: "line" as const }]
          : []),
      ]}
    />

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
