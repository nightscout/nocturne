<script lang="ts">
  import {
    DEFAULT_CONFIG,
    type ExtendedAnalysisConfig,
  } from "$lib/utils/glucose-analytics.ts";
  import { BarChart, Tooltip } from "layerchart";
  import { calculateTimeInRange } from "$lib/utils/calculate/time-in-range.ts";
  import type { Entry } from "$lib/api";
  let {
    entries,
    config,
  }: { entries: Entry[]; config?: ExtendedAnalysisConfig } = $props();
  const timeInRange = $derived(
    calculateTimeInRange(entries, {
      ...DEFAULT_CONFIG,
      ...config,
    })
  );

  const chartConfig = {
    severeLow: {
      key: "severeLow",
      label: "Severe Low",
      color: "var(--severe-low-bg)",
    },
    low: {
      key: "low",
      label: "Low",
      color: "var(--low-bg)",
    },
    target: {
      key: "target",
      label: "Target",
      color: "var(--target-bg)",
    },
    high: {
      key: "high",
      label: "High",
      color: "var(--high-bg)",
    },
    severeHigh: {
      key: "severeHigh",
      label: "Severe High",
      color: "var(--severe-high-bg)",
    },
  };
</script>

<BarChart
  data={[timeInRange.percentages]}
  yDomain={[0, 100]}
  x={() => 1}
  yBaseline={undefined}
  yNice={false}
  series={Object.values(chartConfig).map((c) => ({
    key: c.key,
    color: c.color,
    label: c.label,
  }))}
  legend
  seriesLayout="stack"
  padding={{ top: 0, bottom: 24 }}
  props={{
    bars: {
      motion: { type: "tween", duration: 200 },
      strokeWidth: 0,
    },

    tooltip: {
      hideTotal: true,
      context: { mode: "bounds" },
      // header: { format: "none" },
    },
    xAxis: {
      hidden: true,
    },
    yAxis: {
      hidden: true,
    },
  }}
>
  {#snippet tooltip({ context: _ })}
    <Tooltip.Root>
      {#snippet children({ data })}
        <Tooltip.List>
          <Tooltip.Item label="Label:" value={data.label} />
          <!-- <Tooltip.Item label="Range:" value="{data.start} - {data.end}" /> -->
        </Tooltip.List>
      {/snippet}
    </Tooltip.Root>
  {/snippet}
</BarChart>
