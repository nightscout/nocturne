<script lang="ts">
  /**
   * Stacked bar chart for calendar day cells showing Time in Range distribution.
   * Uses layerchart BarChart with vertical stacking.
   */
  import { BarChart } from "layerchart";

  interface Props {
    /** Percentages for each glucose range */
    lowPercent: number;
    inRangePercent: number;
    highPercent: number;
    /** Click handler */
    onclick?: () => void;
  }

  let { lowPercent, inRangePercent, highPercent, onclick }: Props = $props();

  // Transform data for layerchart stacked bar format
  const chartData = $derived([
    {
      category: "tir",
      low: lowPercent,
      inRange: inRangePercent,
      high: highPercent,
    },
  ]);

  // Series configuration for stacking (bottom to top: low, in-range, high)
  const series = [
    { key: "low", color: "var(--glucose-low)" },
    { key: "inRange", color: "var(--glucose-in-range)" },
    { key: "high", color: "var(--glucose-high)" },
  ];
</script>

<button
  type="button"
  class="w-full h-full cursor-pointer hover:scale-105 transition-transform focus:outline-none focus:ring-2 focus:ring-primary rounded"
  {onclick}
>
  <BarChart
    data={chartData}
    x="category"
    {series}
    seriesLayout="stack"
    axis={false}
    grid={false}
    legend={false}
    tooltip={false}
    padding={{ left: 0, right: 0, top: 0, bottom: 0 }}
    props={{
      bars: {
        strokeWidth: 0,
        radius: 4,
      },
    }}
  />
</button>
