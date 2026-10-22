<script lang="ts">
  /**
   * Stacked bar chart for calendar day cells showing Time in Range distribution.
   * Uses layerchart BarChart with vertical stacking.
   */
  import { BarChart } from "layerchart";

  interface Props {
    /** Percentages for each glucose range */
    veryLowPercent: number;
    lowPercent: number;
    inRangePercent: number;
    highPercent: number;
    veryHighPercent: number;
    /** Click handler */
    onclick?: () => void;
  }

  let {
    veryLowPercent,
    lowPercent,
    inRangePercent,
    highPercent,
    veryHighPercent,
    onclick,
  }: Props = $props();

  // Transform data for layerchart stacked bar format
  const chartData = $derived([
    {
      category: "tir",
      veryLow: veryLowPercent,
      low: lowPercent,
      inRange: inRangePercent,
      high: highPercent,
      veryHigh: veryHighPercent,
    },
  ]);

  // Series configuration for stacking (bottom to top: very low to very high)
  const series = [
    { key: "veryLow", color: "var(--glucose-very-low)" },
    { key: "low", color: "var(--glucose-low)" },
    { key: "inRange", color: "var(--glucose-in-range)" },
    { key: "high", color: "var(--glucose-high)" },
    { key: "veryHigh", color: "var(--glucose-very-high)" },
  ];
</script>

<!-- eslint-disable-next-line no-restricted-syntax -- the whole chart is the click target -->
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
    tooltipContext={false}
    padding={{ left: 0, right: 0, top: 0, bottom: 0 }}
    props={{
      bars: {
        strokeWidth: 0,
        radius: 4,
      },
    }}
  />
</button>
