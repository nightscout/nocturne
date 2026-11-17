<script lang="ts">
  import { BarChart } from "layerchart";
  import type { HourlyInsulinDeliveryPoint } from "#lib/api/index.js";

  interface Props {
    /** The hours of the report period, in the patient's timezone */
    hours: HourlyInsulinDeliveryPoint[];
  }

  let { hours }: Props = $props();

  const chartData = $derived(
    hours.map((h) => ({
      hour: `${(h.hour ?? 0).toString().padStart(2, "0")}:00`,
      avg: h.manualBolusesPerDay ?? 0,
    }))
  );

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
