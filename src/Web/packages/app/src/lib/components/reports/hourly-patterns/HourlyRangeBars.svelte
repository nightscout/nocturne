<!--
  One 100% stacked bar per hour of the day, split into the consensus bands the
  API returns. Bars rather than an area: each hour is its own bucket, and the
  summary above names hours the reader should be able to find here.
-->
<script lang="ts">
  import { BarChart } from "layerchart";
  import type { HourlyPattern } from "$lib/api";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import { hourlyBandSeries } from "../hourly-bands";
  import { hourSpan, hourStart } from "./hour-labels";

  interface Props {
    hours: HourlyPattern[];
  }

  let { hours }: Props = $props();

  const series = $derived(hourlyBandSeries());

  const data = $derived(
    hours.map((h) => ({
      hour: h.hour ?? 0,
      veryLow: h.timeInRange?.veryLow ?? 0,
      low: h.timeInRange?.low ?? 0,
      tightTarget: h.timeInRange?.tightTarget ?? 0,
      aboveTightTarget: h.timeInRange?.aboveTightTarget ?? 0,
      high: h.timeInRange?.high ?? 0,
      veryHigh: h.timeInRange?.veryHigh ?? 0,
    }))
  );

  // Every third hour is labelled; 24 labels overlap at phone width.
  const axisLabel = (hour: number) => (hour % 3 === 0 ? hourStart(hour) : "");
</script>

<figure class="m-0 space-y-3">
  <div class="h-[240px] w-full @md:h-[300px] print:h-[260px]">
    <BarChart
      {data}
      x="hour"
      {series}
      seriesLayout="stack"
      bandPadding={0.2}
      yDomain={[0, 100]}
      padding={{ top: 8, right: 8, bottom: 28, left: 40 }}
      props={{
        xAxis: { format: axisLabel },
        yAxis: { format: (v: number) => `${v}%`, ticks: [0, 25, 50, 75, 100] },
        bars: { strokeWidth: 0 },
        tooltip: {
          header: { format: (v: number) => hourSpan(v) },
          item: { format: (v: number) => `${v}%` },
          hideTotal: true,
        },
      }}
    />
  </div>
  <ChartKey items={series.map((s) => ({ texture: s.texture, label: s.label }))} />
</figure>
