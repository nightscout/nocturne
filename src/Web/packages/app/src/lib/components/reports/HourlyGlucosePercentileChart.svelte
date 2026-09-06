<script lang="ts">
  import { AreaChart } from "layerchart";
  import { scaleLinear } from "d3-scale";

  interface HourlyPercentileData {
    hour: number;
    p10: number;
    p25: number;
    median: number;
    p75: number;
    p90: number;
  }

  let {
    data,
  }: {
    data: HourlyPercentileData[];
  } = $props();
</script>

<div class="w-full">
  {#if data.length > 0}
    <div class="h-[400px] p-4 border rounded-sm">
      <AreaChart
        {data}
        x={(d) => d.hour}
        series={[
          {
            key: "p10",
            value: (d: HourlyPercentileData) => d.p10,
            color: "#a0a0FF",
            label: "10th-90th percentile",
          },
          {
            key: "p25",
            value: (d: HourlyPercentileData) => d.p25,
            color: "#000055",
            label: "25th-75th percentile",
          },
          {
            key: "median",
            value: (d: HourlyPercentileData) => d.median,
            color: "#000000",
            label: "Median",
          },
          {
            key: "p75",
            value: (d: HourlyPercentileData) => d.p75,
            color: "#000055",
          },
          {
            key: "p90",
            value: (d: HourlyPercentileData) => d.p90,
            color: "#a0a0FF",
          },
        ]}
        xDomain={[0, 23]}
        yDomain={[0, 400]}
        xScale={scaleLinear()}
        yScale={scaleLinear()}
        padding={{ top: 20, right: 20, bottom: 40, left: 60 }}
      />
    </div>
  {:else}
    <div
      class="flex items-center justify-center h-[400px] text-muted-foreground border rounded-sm"
    >
      <div class="text-center">
        <p class="text-lg font-medium">No data available</p>
        <p class="text-sm">
          No glucose readings found for percentile visualization
        </p>
      </div>
    </div>
  {/if}
</div>

<style>
  /* Custom styles for better visualization */
  :global(.target-lines) {
    pointer-events: none;
  }
</style>
