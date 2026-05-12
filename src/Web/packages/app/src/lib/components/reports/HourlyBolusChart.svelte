<script lang="ts">
  import { BarChart } from "layerchart";
  import type { Bolus } from "$lib/api";

  interface Props {
    /** Raw bolus records for the report period */
    boluses: Bolus[];
    /** Number of days in the report period (used to compute averages) */
    dayCount: number;
  }

  let { boluses, dayCount }: Props = $props();

  /** Bucket boluses by hour-of-day and compute average count per hour */
  const chartData = $derived.by(() => {
    const counts = new Array<number>(24).fill(0);

    for (const b of boluses) {
      if (b.mills == null) continue;
      const hour = new Date(b.mills).getHours();
      counts[hour] += 1;
    }

    const divisor = Math.max(dayCount, 1);

    return counts.map((count, hour) => ({
      hour: `${hour.toString().padStart(2, "0")}:00`,
      avg: Math.round((count / divisor) * 100) / 100,
    }));
  });

  const hasData = $derived(chartData.some((d) => d.avg > 0));
</script>

<div class="w-full">
  {#if hasData}
    <div class="h-[80px] w-full">
      <BarChart
        data={chartData}
        x="hour"
        series={[
          {
            key: "avg",
            color: "var(--insulin-bolus)",
            label: "Avg bolus count",
          },
        ]}
        props={{
          xAxis: {
            format: (d: string) => {
              const h = parseInt(d, 10);
              if (h % 6 !== 0) return "";
              if (h === 0) return "12a";
              if (h < 12) return `${h}a`;
              if (h === 12) return "12p";
              return `${h - 12}p`;
            },
          },
        }}
        padding={{ top: 4, right: 20, bottom: 20, left: 20 }}
      />
    </div>
  {:else}
    <div
      class="flex h-[80px] w-full items-center justify-center text-muted-foreground"
    >
      <p class="text-xs">No bolus data</p>
    </div>
  {/if}
</div>
