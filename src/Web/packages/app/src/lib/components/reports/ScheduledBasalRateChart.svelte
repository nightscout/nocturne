<script lang="ts">
  import type { ScheduleEntry } from "$lib/api/generated/nocturne-api-client";
  import { AreaChart } from "layerchart";
  import { curveStepAfter } from "d3";

  interface Props {
    entries: ScheduleEntry[];
  }

  let { entries }: Props = $props();

  function getTimeAsSeconds(entry: ScheduleEntry): number {
    if (entry.timeAsSeconds != null) return entry.timeAsSeconds;
    if (!entry.time) return 0;
    const parts = entry.time.split(":");
    const hours = parseInt(parts[0] ?? "0", 10);
    const minutes = parseInt(parts[1] ?? "0", 10);
    const seconds = parseInt(parts[2] ?? "0", 10);
    return hours * 3600 + minutes * 60 + seconds;
  }

  const chartData = $derived.by(() => {
    if (entries.length === 0) return [];

    const sorted = [...entries].sort(
      (a, b) => getTimeAsSeconds(a) - getTimeAsSeconds(b),
    );

    return sorted.map((entry) => ({
      hour: getTimeAsSeconds(entry) / 3600,
      rate: entry.value ?? 0,
    }));
  });

  function niceMax(val: number): number {
    if (val <= 0.5) return 0.5;
    if (val <= 1) return 1;
    if (val <= 1.5) return 1.5;
    if (val <= 2) return 2;
    if (val <= 2.5) return 2.5;
    if (val <= 3) return 3;
    return Math.ceil(val);
  }

  const yMax = $derived.by(() => {
    if (chartData.length === 0) return 1;
    const maxRate = Math.max(...chartData.map((d) => d.rate), 0.1);
    return niceMax(maxRate);
  });

  const hasData = $derived(chartData.length > 0);
</script>

{#if hasData}
  <AreaChart
    data={chartData}
    x="hour"
    series={[
      {
        key: "rate",
        color: "var(--insulin-scheduled-basal)",
      },
    ]}
    xDomain={[0, 23]}
    yDomain={[0, yMax]}
    renderContext="svg"
    padding={{ top: 4, right: 20, bottom: 2, left: 20 }}
    props={{
      area: { curve: curveStepAfter, fillOpacity: 0.2 },
      xAxis: { format: () => "" },
    }}
  />
{:else}
  <div class="flex h-full items-center justify-center text-muted-foreground text-xs">
    No scheduled basal rate data
  </div>
{/if}
